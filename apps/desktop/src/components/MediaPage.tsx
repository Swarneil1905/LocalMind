import { useCallback, useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { Film, Image as ImageIcon, Download, RefreshCw } from "lucide-react";
import "./media.css";
import { CloudImageStudio } from "./CloudImageStudio";
import { PageArt } from "./PageArt";

export interface MediaJob {
  id: string;
  kind: "image" | "image-install" | "video" | "cloud-image";
  state: "review" | "submitting" | "unknown" | "queued" | "generating" | "downloading" | "paused" | "failed" | "cancelled" | "completed";
  prompt: string;
  model: string;
  license: string;
  location: string;
  progress: number;
  message: string;
  provider_id: string | null;
  output: boolean;
  cost_cents: number;
  duration?: number;
  ratio?: string;
  revision?: string;
  pricing_date?: string;
  created_at: string;
}
interface Catalog {
  images: { enabled: boolean; runtime_ready: boolean; installed: boolean; reason: string; requirements: string; download_bytes: number };
  credential: "saved" | "missing" | "locked";
  jobs: MediaJob[];
  reserved_cents: number;
  limit_cents: number;
  pricing_date: string;
}
function request<T>(operation: string, payload?: Record<string, unknown>) {
  return invoke<T>("media_request", { operation, payload: payload ?? null });
}
const money = (cents: number) => `$${(cents / 100).toFixed(2)}`;
const active = new Set(["queued", "generating", "downloading", "submitting"]);

export function MediaPage() {
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState(false);
  const [token, setToken] = useState("");
  const [prompt, setPrompt] = useState("");
  const [imagePrompt, setImagePrompt] = useState("");
  const [source, setSource] = useState("");
  const [duration, setDuration] = useState<5 | 10>(5);
  const [ratio, setRatio] = useState("1280:720");
  const [review, setReview] = useState<MediaJob | null>(null);
  const [reviewSource, setReviewSource] = useState("");
  const [consent, setConsent] = useState(false);
  const [downloadConsent, setDownloadConsent] = useState(false);
  const [previews, setPreviews] = useState<Record<string, string>>({});
  const epoch = useRef(0);
  const working = useRef(false);
  const fileVersion = useRef(0);
  const reviewHeading = useRef<HTMLHeadingElement>(null);
  const load = useCallback(async (generation: number) => {
    const result = await request<Catalog>("status");
    if (generation === epoch.current) setCatalog(result);
  }, []);
  useEffect(() => {
    const generation = ++epoch.current;
    let timer: ReturnType<typeof setTimeout>;
    async function poll() {
      try { await load(generation); }
      catch { if (generation === epoch.current) setError("Media status is unavailable. Keep this page open to reconnect."); }
      if (generation === epoch.current) timer = setTimeout(() => void poll(), 3000);
    }
    void poll();
    return () => { epoch.current = generation + 1; clearTimeout(timer); };
  }, [load]);
  useEffect(() => { if (review) reviewHeading.current?.focus(); }, [review]);

  async function action(work: () => Promise<void>) {
    if (working.current) return;
    working.current = true;
    const generation = epoch.current;
    setBusy(true); setError(""); setNotice("");
    try { await work(); }
    catch (reason) {
      if (generation === epoch.current) setError(typeof reason === "string" ? reason : "The media request could not be completed. Check its saved job status before trying again.");
    } finally {
      if (generation === epoch.current) {
        await load(generation).catch(() => {});
        setBusy(false);
      }
      working.current = false;
    }
  }
  async function showReview(job: MediaJob) {
    const generation = epoch.current;
    const result = await request<{ image: string }>("preview", { job_id: job.id });
    if (generation !== epoch.current) return;
    setReview(job); setReviewSource(result.image); setConsent(false);
  }
  async function chooseFile(file?: File) {
    const generation = ++fileVersion.current;
    const view = epoch.current;
    setSource(""); setError("");
    if (!file) return;
    if (file.type !== "image/png" || file.size > 3_500_000) {
      setError("Choose a PNG smaller than 3.5 MB. Export other image formats as PNG first."); return;
    }
    const reader = new FileReader();
    reader.onload = () => { if (generation === fileVersion.current && view === epoch.current) setSource(String(reader.result)); };
    reader.onerror = () => { if (generation === fileVersion.current && view === epoch.current) setError("The selected image could not be read."); };
    reader.readAsDataURL(file);
  }

  const localBusy = catalog?.jobs.some(job => job.kind !== "video" && active.has(job.state));
  return <div className="media-page">
    <header className="media-intro"><span className="page-eyebrow">CREATE & KEEP</span><h1>Media studio</h1>
      <p>Explore images on your device. Review every video before sending it to Runway.</p>
      <PageArt kind="media" /></header>
    {error && <p className="page-notice error" role="alert">{error}</p>}
    {notice && <p className="page-notice" role="status">{notice}</p>}
    {!catalog ? <p role="status">Connecting to your media library…</p> : <>
      <div className="media-create-grid">
        <CloudImageStudio jobs={catalog.jobs} />
        <section className="media-card" aria-labelledby="image-studio-title">
          <header><span className="media-tile"><ImageIcon size={18} aria-hidden="true" /></span><h2 id="image-studio-title">Local images</h2><span className="page-badge">Experimental</span></header>
          <p>{catalog.images.reason}</p><p className="muted">{catalog.images.requirements}</p>
          <p className="muted">SSD-1B · 512 × 512 · Apache-2.0 model card</p>
          {catalog.images.runtime_ready && <>
            {!catalog.images.installed ? <>
              <label className="media-check"><input type="checkbox" checked={downloadConsent} disabled={busy || localBusy} onChange={e => setDownloadConsent(e.target.checked)} />
                Download {(catalog.images.download_bytes / 1024 ** 3).toFixed(2)} GiB of optional weights from Hugging Face. I have reviewed the <a href="https://huggingface.co/segmind/SSD-1B" target="_blank" rel="noreferrer">model card</a>.</label>
              <button className="page-button" disabled={busy || localBusy || !downloadConsent} onClick={() => void action(async () => { await request("install", {}); setDownloadConsent(false); })}>Download image model</button>
            </> : <>
              <label className="media-field">Describe an image<textarea rows={3} maxLength={1000} value={imagePrompt} disabled={busy} onChange={e => setImagePrompt(e.target.value)} placeholder="A quiet reading nook beside a rain-streaked window" /></label>
              <button className="page-button" disabled={busy || localBusy || !imagePrompt.trim()} onClick={() => void action(async () => { await request("image", { prompt: imagePrompt }); })}>Generate on this device</button>
            </>}
          </>}
        </section>
        <section className="media-card" aria-labelledby="video-studio-title">
          <header><span className="media-tile"><Film size={18} aria-hidden="true" /></span><h2 id="video-studio-title">Image to video</h2><span className="page-badge">Runway cloud</span></header>
          <p>Animate one image with Gen-4 Turbo. Estimated {money(duration * 5)} for {duration} seconds, before tax. No subscription is included.</p>
          <details><summary>Runway connection · {catalog.credential === "saved" ? "key saved" : catalog.credential === "locked" ? "credential store locked" : "key needed"}</summary>
            <p>Your key stays in your operating system credential store. It is checked by Runway when you submit a job.</p>
            <label className="media-field">Runway API key<input type="password" autoComplete="off" maxLength={1000} value={token} onChange={e => setToken(e.target.value)} disabled={busy} /></label>
            <div className="media-actions"><button className="page-button" disabled={busy || !token.trim()} onClick={() => void action(async () => { const value = token; setToken(""); await request("credential", { token: value }); })}>Save key</button>
              <button className="page-button" disabled={busy || catalog.credential !== "saved"} onClick={() => void action(async () => { await request("credential", { token: "" }); setToken(""); })}>Remove key</button></div>
          </details>
          <label className="media-field">Starting image<input type="file" accept="image/png" disabled={busy || !!review} onChange={e => void chooseFile(e.target.files?.[0])} /></label>
          {source && !review && <img className="media-source" src={source} alt="Selected starting frame" />}
          <label className="media-field">Motion prompt<textarea rows={3} maxLength={1000} value={prompt} disabled={busy || !!review} onChange={e => setPrompt(e.target.value)} placeholder="The camera slowly moves toward the window as the rain falls." /></label>
          <div className="media-options"><label className="media-field">Duration<select value={duration} disabled={busy || !!review} onChange={e => setDuration(Number(e.target.value) as 5 | 10)}><option value={5}>5 seconds · $0.25</option><option value={10}>10 seconds · $0.50</option></select></label>
            <label className="media-field">Format<select value={ratio} disabled={busy || !!review} onChange={e => setRatio(e.target.value)}><option value="1280:720">Landscape · 16:9</option><option value="720:1280">Portrait · 9:16</option><option value="960:960">Square · 1:1</option></select></label></div>
          <button className="page-button" disabled={busy || !!review || !source || !prompt.trim()} onClick={() => void action(async () => { const job = await request<MediaJob>("prepare", { prompt, source, duration, ratio }); await showReview(job); })}>Review video request</button>
          <p className="muted">Nothing leaves this device until you confirm below. {money(catalog.reserved_cents)} of {money(catalog.limit_cents)} reserved this month by LocalMind. This is a local limit, not your Runway balance.</p>
        </section>
      </div>
      {review && <section className="media-card media-review" aria-labelledby="media-review-title">
        <h2 id="media-review-title" ref={reviewHeading} tabIndex={-1}>Review before sending</h2>
        <div className="media-review-content"><img className="media-source" src={reviewSource} alt="Exact image to be sent to Runway" /><div>
          <p className="media-prompt">{review.prompt}</p><p>Runway · Gen-4 Turbo · {review.duration} seconds · {review.ratio}</p>
          <p>Estimated cost: <strong>{money(review.cost_cents)} USD before tax</strong>. Pricing checked {review.pricing_date ?? catalog.pricing_date}; Runway controls the final charge.</p>
          <p>The image and prompt will leave this device. Generated output is subject to <a href="https://runway.com/terms-of-use" target="_blank" rel="noreferrer">Runway’s terms</a> and <a href="https://runway.com/privacy-policy" target="_blank" rel="noreferrer">privacy policy</a>.</p>
          <label className="media-check"><input type="checkbox" checked={consent} disabled={busy} onChange={e => setConsent(e.target.checked)} />I have rights to this image and approve this upload and estimated charge.</label>
          <div className="media-actions"><button className="page-button" disabled={busy || !consent || catalog.credential !== "saved"} onClick={() => void action(async () => { await request("submit", { job_id: review.id }); setReview(null); setConsent(false); setNotice("Submission recorded. Follow its status in your jobs below."); })}>Send to Runway · {money(review.cost_cents)}</button>
            <button className="page-button" disabled={busy} onClick={() => void action(async () => { await request("cancel", { job_id: review.id }); setReview(null); setConsent(false); })}>Dismiss review</button></div>
          {catalog.credential !== "saved" && <p>Add a Runway API key above to submit.</p>}
        </div></div>
      </section>}
      <section className="media-library" aria-labelledby="media-jobs-title"><header><h2 id="media-jobs-title">Your jobs</h2><span className="muted">Saved on this device · latest 100</span></header>
        {catalog.jobs.length === 0 && <div className="media-empty"><PageArt kind="media" /><h3>A place for what you create</h3><p>Your previews, generated images and video jobs will appear here.</p></div>}
        <div className="media-jobs">{catalog.jobs.filter(job => job.kind !== "cloud-image").map(job => <article className="media-card" key={job.id} aria-label={`${job.kind} job: ${job.prompt}`}>
          <header><span className="page-badge">{job.location}</span><strong>{job.state}</strong></header>
          <h3>{job.kind === "image-install" ? "Image model download" : job.prompt}</h3>
          {active.has(job.state) && <progress max={100} value={job.progress || undefined} aria-label="Job progress" />}
          <p>{job.message}</p>
          {job.state === "unknown" && <p>Do not create a replacement until you check the original task and charges in Runway.</p>}
          {previews[job.id] && <img className="media-result" src={previews[job.id]} alt={`Generated: ${job.prompt}`} />}
          <p className="muted">{job.model} · {job.license}{job.output && " · Generated"}</p>
          {job.provider_id && <details><summary>Provider receipt</summary><code>{job.provider_id}</code><p>Saved before polling. Resume only checks this task.</p></details>}
          <div className="media-actions">
            {job.state === "review" && <button className="page-button" disabled={busy} onClick={() => void action(() => showReview(job))}>Review</button>}
            {job.kind !== "video" && active.has(job.state) && <button className="page-button" disabled={busy} onClick={() => void action(async () => { await request("cancel", { job_id: job.id }); })}>Cancel</button>}
            {job.state === "paused" && job.provider_id && <button className="page-button" disabled={busy} onClick={() => void action(async () => { await request("resume", { job_id: job.id }); })}><RefreshCw size={14} aria-hidden="true" />Resume status checks</button>}
            {job.output && job.kind === "image" && !previews[job.id] && <button className="page-button" disabled={busy} onClick={() => void action(async () => { const result = await request<{ image: string }>("preview", { job_id: job.id }); setPreviews(values => ({ ...values, [job.id]: result.image })); })}>View image</button>}
            {job.output && job.state === "completed" && <button className="page-button" disabled={busy} onClick={() => void action(async () => { const saved = await invoke<boolean>("export_media", { jobId: job.id, kind: job.kind === "cloud-image" ? "image" : job.kind }); if (saved) setNotice("Generated media saved to your selected location."); })}><Download size={14} aria-hidden="true" />Save {job.kind}</button>}
          </div>
        </article>)}</div>
      </section>
    </>}
  </div>;
}
