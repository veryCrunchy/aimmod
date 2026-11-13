#pragma once
#include <atomic>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <memory>
#include <string>
#include <thread>
#include <Windows.h>

namespace aimmod {
// One bounded input and coalesced output. The worker owns every file handle.
class FileMailbox {
    std::atomic<std::shared_ptr<const std::string>> input, output;
    std::filesystem::path source, destination;
    unsigned ageSeconds, intervalMs;
    std::jthread worker;
public:
    FileMailbox(std::filesystem::path from, std::filesystem::path to, unsigned maxAge = 5, unsigned interval = 20):source(std::move(from)),destination(std::move(to)),ageSeconds(maxAge),intervalMs(interval),worker([this](std::stop_token stop){
        std::uint64_t lastStamp{};
        while(!stop.stop_requested()) {
            try {
                std::string text;
                WIN32_FILE_ATTRIBUTE_DATA info{}; FILETIME now{}; GetSystemTimeAsFileTime(&now);
                const auto ticks=[](FILETIME ft){return (static_cast<std::uint64_t>(ft.dwHighDateTime)<<32)|ft.dwLowDateTime;};
                const bool fresh=GetFileAttributesExW(source.c_str(),GetFileExInfoStandard,&info) && !info.nFileSizeHigh && info.nFileSizeLow<=65536 &&
                    (ticks(now)<=ticks(info.ftLastWriteTime) || ticks(now)-ticks(info.ftLastWriteTime)<=ageSeconds*10000000ull);
                const auto stamp = fresh ? ticks(info.ftLastWriteTime) : 0;
                auto old=input.load();
                if (!old || stamp != lastStamp) {
                bool readOk = !fresh;
                HANDLE file=fresh ? CreateFileW(source.c_str(),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr) : INVALID_HANDLE_VALUE;
                if(file!=INVALID_HANDLE_VALUE) {
                    char bytes[65537]; DWORD count{};
                    if(ReadFile(file,bytes,sizeof(bytes),&count,nullptr) && count<=65536) { text.assign(bytes,count); readOk=true; }
                    CloseHandle(file);
                }
                if(!old || *old!=text) input.store(std::make_shared<const std::string>(std::move(text)));
                lastStamp = readOk ? stamp : 0;
                }
                if(auto next=output.exchange(nullptr); next && !destination.empty()) {
                    auto temp=destination;temp+=L".tmp";
                    std::ofstream out(temp,std::ios::binary|std::ios::trunc);out<<*next;out.close();
                    if(out) MoveFileExW(temp.c_str(),destination.c_str(),MOVEFILE_REPLACE_EXISTING);
                }
            } catch(...) { }
            std::this_thread::sleep_for(std::chrono::milliseconds(intervalMs));
        }
    }) {}
    std::shared_ptr<const std::string> Read() const { return input.load(); }
    void Write(std::string text) { output.store(std::make_shared<const std::string>(std::move(text))); }
};
}
