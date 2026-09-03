import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";
import { convertFileSrc, invoke, isTauri } from "@tauri-apps/api/core";
import { open } from "@tauri-apps/plugin-dialog";
import {
  CaretDown,
  DownloadSimple,
  FolderOpen,
  ImageSquare,
  MagnifyingGlass,
  Package,
  PaintBrush,
  Trash,
  UploadSimple,
  WarningCircle,
} from "@phosphor-icons/react";

interface SkinProvider {
  id: string;
  name: string;
  status: string;
  capabilities: string[];
  message: string;
}

interface SkinScreenshot {
  label?: string | null;
  imageUrl: string;
  width?: number | null;
  height?: number | null;
}

interface SkinItem {
  provider: string;
  sourceId: string;
  name: string;
  creator: string;
  players: string[];
  rulesets: string[];
  aspectRatios: string[];
  tags: string[];
  sensitive: boolean | null;
  thumbnailUrl: string;
  viewCount: number;
  downloadCount: number;
  countsAreApproximate: boolean;
  fileSizeBytes: number;
  fileSizeIsApproximate: boolean;
  submittedAt: string;
  updatedAt: string;
  screenshots: SkinScreenshot[];
  downloadAvailable: boolean;
}

interface SkinSearchResponse {
  provider: string;
  items: SkinItem[];
  nextPageToken?: string | null;
  error?: string | null;
}

interface SkinDetailResponse {
  provider: string;
  item: SkinItem | null;
  error?: string | null;
}

interface InstalledSkin {
  id: string;
  name: string;
  author?: string | null;
  fileName: string;
  sizeBytes: number;
  sha256: string;
  provider?: string | null;
  sourceId?: string | null;
  installedAt?: string | null;
  importStatus: string;
}

interface LazerInstalledSkin {
  id: string;
  name: string;
  creator: string;
  hash: string;
  fileCount: number;
  files: Array<{ filename: string; path: string | null }>;
}

interface SkinInstallResult {
  id?: string | null;
  name?: string | null;
  author?: string | null;
  fileName: string;
  sizeBytes?: number | null;
  sha256?: string | null;
  provider?: string | null;
  sourceId?: string | null;
  status: "installed" | "alreadyInstalled" | "cached" | "rejected" | "unavailable" | "error";
  message: string;
}

interface SkinRemoveResult {
  id: string;
  status: "removed" | "notFound" | "rejected" | "error";
  message: string;
}

type SkinView = "browse" | "installed";

function authorOf(item: SkinItem) {
  return item.creator || "Unknown creator";
}

function imageOf(item: SkinItem) {
  return item.screenshots[0]?.imageUrl || item.thumbnailUrl || null;
}

function formatBytes(bytes?: number | null, approximate = false) {
  if (!bytes) return "Size not supplied";
  const value = bytes >= 1024 * 1024 ? `${(bytes / (1024 * 1024)).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`;
  return approximate ? `~${value}` : value;
}

function countLabel(value?: number | null, approximate = false) {
  if (value === null || value === undefined) return "Not supplied";
  return `${approximate ? "~" : ""}${value.toLocaleString()}`;
}

function providerName(providers: SkinProvider[], id: string) {
  return providers.find((item) => item.id === id)?.name || id;
}

function lazerSkinPreview(item: LazerInstalledSkin) {
  const preferred = item.files.find((file) => /^(selection-mode|menu-background|fail-background|spinner-background)(@2x)?\.(png|jpe?g)$/i.test(file.filename) && file.path)
    || item.files.find((file) => /\.(png|jpe?g)$/i.test(file.filename) && file.path);
  return preferred?.path ? convertFileSrc(preferred.path) : null;
}

function RemoteImage({ src, alt, className }: { src: string; alt: string; className?: string }) {
  return <img className={className} src={src} alt={alt} loading="lazy" referrerPolicy="no-referrer" onError={(event) => { event.currentTarget.hidden = true; }} />;
}

export function SkinsWorkspace({ onMessage }: { onMessage: (message: string) => void }) {
  const desktop = isTauri();
  const [view, setView] = useState<SkinView>("browse");
  const [query, setQuery] = useState("");
  const [provider, setProvider] = useState("all");
  const [ruleset, setRuleset] = useState("all");
  const [aspectRatio, setAspectRatio] = useState("all");
  const [tag, setTag] = useState("all");
  const [showSensitive, setShowSensitive] = useState(false);
  const [sort, setSort] = useState("relevance");
  const [providers, setProviders] = useState<SkinProvider[]>([]);
  const [skins, setSkins] = useState<SkinItem[]>([]);
  const [installed, setInstalled] = useState<InstalledSkin[]>([]);
  const [lazerInstalled, setLazerInstalled] = useState<LazerInstalledSkin[]>([]);
  const [selectedKey, setSelectedKey] = useState("");
  const [detail, setDetail] = useState<SkinItem | null>(null);
  const [activeScreenshot, setActiveScreenshot] = useState(0);
  const [loading, setLoading] = useState(false);
  const [actionId, setActionId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const refreshInstalled = useCallback(async () => {
    if (!desktop) return;
    try {
      const [managed, lazer] = await Promise.all([
        invoke<InstalledSkin[]>("list_installed_osu_skins"),
        invoke<LazerInstalledSkin[]>("list_installed_osu_lazer_skins"),
      ]);
      setInstalled(managed);
      setLazerInstalled(lazer);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
    }
  }, [desktop]);

  const runSearch = useCallback(async (sourceProviders = providers) => {
    if (!desktop) return;
    setLoading(true);
    setError(null);
    setNotice(null);
    const ids = provider === "all" ? sourceProviders.map((item) => item.id) : [provider];
    if (!ids.length) {
      setSkins([]);
      setError("No skin providers are configured.");
      setLoading(false);
      return;
    }
    try {
      const responses = await Promise.all(ids.map((providerId) => invoke<SkinSearchResponse>("search_osu_skins", {
        request: {
          provider: providerId,
          query: query.trim(),
          pageToken: null,
          limit: 48,
          filters: {
            rulesets: ruleset === "all" ? [] : [ruleset],
            aspectRatio: aspectRatio === "all" ? null : aspectRatio,
            tag: tag === "all" ? null : tag,
            includeSensitive: showSensitive,
          },
          sort,
          direction: "descending",
        },
      })));
      const next = responses.flatMap((response) => response.items || []);
      setSkins(next);
      setSelectedKey((current) => current && next.some((item) => `${item.provider}:${item.sourceId}` === current) ? current : next[0] ? `${next[0].provider}:${next[0].sourceId}` : "");
      const errors = responses.map((response) => response.error).filter((value): value is string => Boolean(value));
      const allProvidersFailed = responses.length > 0 && errors.length === responses.length;
      setError(allProvidersFailed ? errors.join(" ") : null);
      setNotice(errors.length && !allProvidersFailed ? errors.join(" ") : null);
    } catch (reason) {
      setSkins([]);
      setError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setLoading(false);
    }
  }, [aspectRatio, desktop, provider, providers, query, ruleset, showSensitive, sort, tag]);

  useEffect(() => {
    if (!desktop) return;
    let cancelled = false;
    void Promise.all([
      invoke<SkinProvider[]>("get_osu_skin_providers"),
      invoke<InstalledSkin[]>("list_installed_osu_skins"),
      invoke<LazerInstalledSkin[]>("list_installed_osu_lazer_skins"),
    ]).then(([nextProviders, nextInstalled, nextLazerInstalled]) => {
      if (cancelled) return;
      setProviders(nextProviders);
      setInstalled(nextInstalled);
      setLazerInstalled(nextLazerInstalled);
      void runSearch(nextProviders);
    }).catch((reason) => {
      if (!cancelled) setError(reason instanceof Error ? reason.message : String(reason));
    });
    return () => { cancelled = true; };
  }, [desktop]);

  const visibleSkins = useMemo(() => {
    let next = [...skins];
    if (ruleset !== "all") next = next.filter((item) => item.rulesets.some((value) => value.toLowerCase() === ruleset));
    if (aspectRatio !== "all") next = next.filter((item) => item.aspectRatios.includes(aspectRatio));
    if (tag !== "all") next = next.filter((item) => item.tags.some((value) => value.toLowerCase() === tag));
    if (!showSensitive) next = next.filter((item) => item.sensitive !== true);
    if (sort === "mostDownloaded") next.sort((a, b) => (b.downloadCount || 0) - (a.downloadCount || 0));
    if (sort === "mostViewed") next.sort((a, b) => (b.viewCount || 0) - (a.viewCount || 0));
    if (sort === "name") next.sort((a, b) => a.name.localeCompare(b.name));
    return next;
  }, [aspectRatio, ruleset, showSensitive, skins, sort, tag]);

  const selectedBrowse = visibleSkins.find((item) => `${item.provider}:${item.sourceId}` === selectedKey) || visibleSkins[0] || null;
  const selectedInstalled = selectedKey.startsWith("lazer:") ? null : installed.find((item) => item.id === selectedKey) || installed[0] || null;
  const selectedLazerInstalled = lazerInstalled.find((item) => `lazer:${item.id}` === selectedKey) || (selectedKey.startsWith("lazer:") ? lazerInstalled[0] || null : null);

  useEffect(() => {
    if (!desktop || view !== "browse" || !selectedBrowse) {
      if (view === "browse") setDetail(selectedBrowse);
      return;
    }
    let cancelled = false;
    setDetail(selectedBrowse);
    setActiveScreenshot(0);
    void invoke<SkinDetailResponse>("get_osu_skin", { request: { provider: selectedBrowse.provider, sourceId: selectedBrowse.sourceId } })
      .then((response) => {
        if (cancelled) return;
        if (response.item) setDetail(response.item);
        if (response.error) setNotice(response.error);
      })
      .catch((reason) => { if (!cancelled) setError(reason instanceof Error ? reason.message : String(reason)); });
    return () => { cancelled = true; };
  }, [desktop, selectedBrowse?.provider, selectedBrowse?.sourceId, view]);

  const installSkin = useCallback(async (item: SkinItem) => {
    if (!desktop) return;
    const key = `${item.provider}:${item.sourceId}`;
    setActionId(key);
    try {
      const result = await invoke<SkinInstallResult>("install_osu_skin", { request: { provider: item.provider, sourceId: item.sourceId } });
      onMessage(result.message);
      if (result.status === "installed" || result.status === "alreadyInstalled" || result.status === "cached") await refreshInstalled();
    } catch (reason) {
      onMessage(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setActionId(null);
    }
  }, [desktop, onMessage, refreshInstalled]);

  const importFiles = useCallback(async () => {
    if (!desktop) return;
    const selected = await open({ directory: false, multiple: true, title: "Import osu! skins", filters: [{ name: "osu! skins", extensions: ["osk"] }] });
    if (!selected) return;
    const paths = Array.isArray(selected) ? selected : [selected];
    try {
      const results = await invoke<SkinInstallResult[]>("import_osu_skin_files", { paths });
      onMessage(results.map((item) => item.message).join(" "));
      await refreshInstalled();
      setView("installed");
    } catch (reason) {
      onMessage(reason instanceof Error ? reason.message : String(reason));
    }
  }, [desktop, onMessage, refreshInstalled]);

  const removeSkin = useCallback(async (item: InstalledSkin) => {
    if (!desktop) return;
    setActionId(item.id);
    try {
      const result = await invoke<SkinRemoveResult>("remove_installed_osu_skin", { id: item.id });
      onMessage(result.message);
      if (result.status === "removed" || result.status === "notFound") {
        await refreshInstalled();
        setSelectedKey("");
      }
    } catch (reason) {
      onMessage(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setActionId(null);
    }
  }, [desktop, onMessage, refreshInstalled]);

  const selected = detail || selectedBrowse;
  const screenshots = selected?.screenshots.filter((item) => Boolean(item.imageUrl)) || [];
  const heroImage = screenshots[activeScreenshot]?.imageUrl || (selected ? imageOf(selected) : null);
  const allTags = useMemo(() => [...new Set(skins.flatMap((item) => item.tags).map((value) => value.toLowerCase()))].sort().slice(0, 30), [skins]);

  return <div className="osu-skins-shell">
    {!desktop && <div className="osu-data-boundary"><WarningCircle size={18} /><div><strong>Desktop provider connection required</strong><span>This browser route does not load sample skins or installed-library data.</span></div></div>}
    <div className="osu-skins-layout">
      <aside className="osu-skin-filters" aria-label="Skin filters">
        <div className="osu-panel-title"><PaintBrush size={17} /><strong>Skins</strong></div>
        <div className="osu-skin-view-switch" aria-label="Skin library view">
          <button type="button" className={view === "browse" ? "active" : ""} onClick={() => { setView("browse"); setSelectedKey(visibleSkins[0] ? `${visibleSkins[0].provider}:${visibleSkins[0].sourceId}` : ""); }}><MagnifyingGlass size={15} />Browse</button>
          <button type="button" className={view === "installed" ? "active" : ""} onClick={() => { setView("installed"); setSelectedKey(lazerInstalled[0] ? `lazer:${lazerInstalled[0].id}` : installed[0]?.id || ""); }}><Package size={15} />Installed <span>{lazerInstalled.length + installed.length}</span></button>
        </div>
        {view === "browse" && <>
          <label className="osu-filter-field"><span>Provider</span><div className="osu-select-wrap"><select value={provider} onChange={(event) => setProvider(event.target.value)} disabled={!desktop}><option value="all">All providers</option>{providers.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select><CaretDown size={13} /></div></label>
          <label className="osu-filter-field"><span>Ruleset</span><div className="osu-select-wrap"><select value={ruleset} onChange={(event) => setRuleset(event.target.value)} disabled={!desktop}><option value="all">All rulesets</option><option value="osu">osu!standard</option><option value="mania">osu!mania</option><option value="taiko">osu!taiko</option><option value="catch">osu!catch</option></select><CaretDown size={13} /></div></label>
          <label className="osu-filter-field"><span>Aspect ratio</span><div className="osu-select-wrap"><select value={aspectRatio} onChange={(event) => setAspectRatio(event.target.value)} disabled={!desktop}><option value="all">Any ratio</option><option value="16:9">16:9</option><option value="16:10">16:10</option><option value="4:3">4:3</option></select><CaretDown size={13} /></div></label>
          <label className="osu-filter-field"><span>Tag</span><div className="osu-select-wrap"><select value={tag} onChange={(event) => setTag(event.target.value)} disabled={!desktop}><option value="all">Any tag</option>{allTags.map((item) => <option key={item} value={item}>{item}</option>)}</select><CaretDown size={13} /></div></label>
          <label className="osu-check"><input type="checkbox" checked={showSensitive} onChange={(event) => setShowSensitive(event.target.checked)} disabled={!desktop} /><span /><strong>Show sensitive</strong></label>
        </>}
        {view === "installed" && <button type="button" className="osu-secondary-action osu-import-skin" onClick={() => void importFiles()} disabled={!desktop}><FolderOpen size={16} />Import .osk files</button>}
      </aside>

      <section className="osu-skin-results">
        {view === "browse" ? <>
          <form className="osu-search-bar" onSubmit={(event: FormEvent) => { event.preventDefault(); void runSearch(); }}><MagnifyingGlass size={18} /><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search skins, creators, players, or tags" autoComplete="off" disabled={!desktop} /><button type="submit" disabled={!desktop || loading}>{loading ? "Searching" : "Search"}</button></form>
          <div className="osu-result-toolbar"><strong>{visibleSkins.length} skins</strong><div><span>Sort by</span><div className="osu-select-wrap compact"><select value={sort} onChange={(event) => setSort(event.target.value)} disabled={!desktop}><option value="relevance">Relevance</option><option value="mostDownloaded">Downloads</option><option value="mostViewed">Views</option><option value="name">Name</option></select><CaretDown size={12} /></div></div></div>
          {notice && <div className="osu-provider-notice" role="status"><WarningCircle size={16} /><span>Some providers could not be searched. Results from the available providers are shown.</span><details><summary>Details</summary><p>{notice}</p></details></div>}
          {error && <div className="osu-inline-error" role="alert"><WarningCircle size={16} /><span>{error}</span><button type="button" onClick={() => void runSearch()}>Try again</button></div>}
          {visibleSkins.length ? <div className="osu-skin-list">{visibleSkins.map((item) => {
            const key = `${item.provider}:${item.sourceId}`;
            const image = imageOf(item);
            return <article key={key} className={key === selectedKey ? "selected" : ""}>
              <button type="button" className="osu-skin-row-main" onClick={() => setSelectedKey(key)} aria-label={`Open ${item.name} by ${authorOf(item)}`}>
                {image && <RemoteImage src={image} alt={`${item.name} gameplay screenshot`} />}
                <span><strong>{item.name}</strong><small>by {authorOf(item)}</small><em>{item.rulesets.join(" · ") || "Rulesets not supplied"}</em></span>
                <b>{providerName(providers, item.provider)}</b>
              </button>
              <button type="button" className="osu-icon-button" onClick={() => void installSkin(item)} disabled={!desktop || actionId === key || item.downloadAvailable === false} aria-label={`Install ${item.name}`}><DownloadSimple size={17} /></button>
            </article>;
          })}</div> : <div className="osu-empty"><PaintBrush size={34} /><strong>No skins loaded</strong><span>{desktop ? "Search a configured provider or change the filters." : "Open this workspace in AimMod desktop to browse providers."}</span></div>}
        </> : <>
          <div className="osu-section-toolbar"><div><span>osu!lazer library</span><strong>{lazerInstalled.length + installed.length} installed skins</strong></div><button type="button" className="osu-secondary-action" onClick={() => void importFiles()} disabled={!desktop}><UploadSimple size={16} />Import .osk</button></div>
          {error && <div className="osu-inline-error" role="alert"><WarningCircle size={16} /><span>{error}</span></div>}
          {lazerInstalled.length || installed.length ? <div className="osu-installed-list">{lazerInstalled.map((item) => { const preview = lazerSkinPreview(item); return <article key={`lazer:${item.id}`} className={selectedLazerInstalled?.id === item.id ? "selected" : ""}><button type="button" onClick={() => setSelectedKey(`lazer:${item.id}`)}>{preview ? <RemoteImage src={preview} alt="" /> : <PaintBrush size={22} />}<span><strong>{item.name}</strong><small>{item.creator || "Unknown creator"} · osu!lazer library</small></span><b>{item.fileCount.toLocaleString()} files</b></button><button type="button" className="osu-secondary-action" onClick={() => setSelectedKey(`lazer:${item.id}`)}>View</button></article>; })}{installed.map((item) => <article key={item.id} className={selectedInstalled?.id === item.id ? "selected" : ""}><button type="button" onClick={() => setSelectedKey(item.id)}><Package size={22} /><span><strong>{item.name}</strong><small>{item.author || "Unknown creator"} · AimMod package cache</small></span><b>{formatBytes(item.sizeBytes)}</b></button><button type="button" className="osu-secondary-action" onClick={() => setSelectedKey(item.id)}>Manage</button></article>)}</div> : <div className="osu-empty"><Package size={34} /><strong>No installed skins found</strong><span>Import an .osk file or install a skin from a configured provider.</span></div>}
        </>}
      </section>

      {view === "browse" ? <aside className="osu-skin-detail">
        {selected ? <>
          {heroImage ? <RemoteImage className="osu-skin-hero" src={heroImage} alt={`${selected.name} gameplay screenshot`} /> : <div className="osu-skin-no-image"><ImageSquare size={26} /><span>No screenshot supplied by this provider.</span></div>}
          <div className="osu-skin-heading"><span>{providerName(providers, selected.provider)}</span><h2>{selected.name}</h2><p>by {authorOf(selected)}</p></div>
          {screenshots.length > 1 && <div className="osu-skin-thumbnails" aria-label="Skin screenshots">{screenshots.map((shot, index) => <button type="button" className={activeScreenshot === index ? "active" : ""} key={`${shot.imageUrl}:${index}`} onClick={() => setActiveScreenshot(index)} aria-label={shot.label || `Screenshot ${index + 1}`}><RemoteImage src={shot.imageUrl} alt="" /></button>)}</div>}
          <div className="osu-detail-tags">{selected.rulesets.map((item) => <span key={item}>{item}</span>)}{selected.aspectRatios.map((item) => <span key={item}>{item}</span>)}</div>
          <div className="osu-skin-stats"><div><span>Views</span><strong>{countLabel(selected.viewCount, selected.countsAreApproximate)}</strong></div><div><span>Downloads</span><strong>{countLabel(selected.downloadCount, selected.countsAreApproximate)}</strong></div><div><span>Package</span><strong>{formatBytes(selected.fileSizeBytes, selected.fileSizeIsApproximate)}</strong></div></div>
          <div className="osu-section-label">Tags</div><div className="osu-chip-row">{selected.tags.length ? selected.tags.map((item) => <span key={item}>{item}</span>) : <span>No tags supplied</span>}</div>
          {selected.downloadAvailable === false && <div className="osu-skin-note"><WarningCircle size={16} /><span>This provider did not supply an installable skin package.</span></div>}
          <button type="button" className="osu-primary-action" onClick={() => void installSkin(selected)} disabled={!desktop || actionId === `${selected.provider}:${selected.sourceId}` || !selected.downloadAvailable}><DownloadSimple size={20} />{actionId === `${selected.provider}:${selected.sourceId}` ? "Installing" : "Install in osu!lazer"}</button>
        </> : <div className="osu-empty"><ImageSquare size={34} /><strong>Select a skin</strong><span>Provider screenshots and package details appear here.</span></div>}
      </aside> : <aside className="osu-skin-detail">
        {selectedLazerInstalled ? <div className="osu-installed-detail">{lazerSkinPreview(selectedLazerInstalled) ? <RemoteImage className="osu-skin-hero" src={lazerSkinPreview(selectedLazerInstalled)!} alt={`${selectedLazerInstalled.name} skin asset`} /> : <PaintBrush size={34} />}<span>Installed in osu!lazer</span><h2>{selectedLazerInstalled.name}</h2><p>{selectedLazerInstalled.creator || "Unknown creator"}</p><dl><div><dt>Library</dt><dd>Read directly from client.realm</dd></div><div><dt>Files</dt><dd>{selectedLazerInstalled.fileCount.toLocaleString()}</dd></div><div><dt>Hash</dt><dd>{selectedLazerInstalled.hash || "Not supplied"}</dd></div><div><dt>Examples</dt><dd>{selectedLazerInstalled.files.slice(0, 4).map((file) => file.filename).join(" · ")}</dd></div></dl></div> : selectedInstalled ? <div className="osu-installed-detail"><Package size={34} /><span>Installed skin</span><h2>{selectedInstalled.name}</h2><p>{selectedInstalled.author || "Unknown creator"}</p><dl><div><dt>File</dt><dd>{selectedInstalled.fileName}</dd></div><div><dt>Size</dt><dd>{formatBytes(selectedInstalled.sizeBytes)}</dd></div><div><dt>Imported</dt><dd>{selectedInstalled.installedAt || "Date not supplied"}</dd></div><div><dt>Source</dt><dd>{selectedInstalled.provider || "Local file"}</dd></div></dl><button type="button" className="osu-danger-action" onClick={() => void removeSkin(selectedInstalled)} disabled={!desktop || actionId === selectedInstalled.id}><Trash size={18} />{actionId === selectedInstalled.id ? "Removing" : "Remove from AimMod"}</button></div> : <div className="osu-empty"><Package size={34} /><strong>Select an installed skin</strong><span>Package details and library actions appear here.</span></div>}
      </aside>}
    </div>
  </div>;
}
