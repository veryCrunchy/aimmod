using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

sealed partial class MultiplayerService
{
    readonly CsKeyReader voteKeys = new();
    long returnRequestedAt;

    void StepMatchControls()
    {
        var yes = voteKeys.PressedCode(0x70); var no = voteKeys.PressedCode(0x71);
        if (Current?.Match is not { Phase: MatchPhases.Live } match || ChatCapturing || buyOpen || !voteKeys.Foreground()) return;
        if (!voteKeys.DownCode(0x11) && !voteKeys.DownCode(0x12) && !voteKeys.DownCode(0x10) && match.Vote is { } vote && vote.Eligible.Contains(SelfId) && !vote.Yes.Contains(SelfId) && !vote.No.Contains(SelfId) && (yes || no))
            Command("match-control", JsonSerializer.SerializeToElement(new { command = yes ? "yes" : "no" }));
        if (match.Cs?.Players.Any(p => p.Member == SelfId && p.Afk) == true && clock() - returnRequestedAt >= 1000
            && MatchControlKeys.ReturnHeld(Binds?.Invoke() ?? GameBinds.Bound.Standard, voteKeys.DownCode))
        {
            returnRequestedAt = clock();
            Command("match-control", JsonSerializer.SerializeToElement(new { command = "return" }));
        }
    }

    object? VoteHud()
    {
        if (Current?.Match is not { Vote: { } vote } match) return null;
        var team = match.Cs?.Players.FirstOrDefault(p => p.Member == SelfId)?.Team
            ?? match.Combat?.Players.FirstOrDefault(p => p.Member == SelfId)?.Team;
        if (team != vote.Team) return null;
        return new { vote.Label, yes = vote.Yes.Count, no = vote.No.Count, vote.Required,
            left = Math.Max(0, (int)Math.Ceiling((vote.EndsAt - clock() - HostOffset()) / 1000.0)),
            canVote = vote.Eligible.Contains(SelfId) && !vote.Yes.Contains(SelfId) && !vote.No.Contains(SelfId) };
    }
}
