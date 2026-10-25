# Atlas

A public research document archive. You upload material (PDFs, images, video, audio, text and Office files), review it in
a private queue, and publish only what you approve. Visitors can browse, search, view, download and verify what is
published. Nobody but the admin can upload or publish.

Atlas is content-neutral: it is a tool, and this repository contains no archive content.

## How it works: review before publish

```
upload ──► processing ──► pending ──► (edit, redact, verify) ──► Approve ──► publishing ──► published
                              │                                                         │
                              └──────────► Reject ◄────────────────── Unpublish ◄───────┘
```

* **Uploads land in a private queue.** Nothing is visible to the public (pages, API, files, thumbnails, search) until the admin approves it.
* **Review** each item: edit the title, description, tags and date; record **source notes** (internal only, never shown publicly and never part of any public page or API); set a **verification status** (`unverified`, `partially verified`, `verified`, shown publicly); draw **redaction** boxes on images and PDFs.
* **Approve** builds a separate *public copy* (metadata stripped, redactions burned in) and publishes it. The original stays private. **Unpublish** or **Reject** takes it offline again.
* Publishing is **fail-closed**: if a safe public copy can't be produced, the item stays unpublished and the admin sees why.

## Features

| Area | What you get |
| --- | --- |
| Formats | PDF; images (jpg, png, gif, webp, bmp, tiff); video (mp4, m4v, mov, webm, mkv); audio (mp3, m4a, wav, ogg, opus, flac, aac); text (txt, md, csv, json, log); Office (docx, xlsx, pptx, odt, ods, odp). Anything else (HTML, SVG, executables, legacy `.doc`) is refused. |
| Search | SQLite FTS5 with stemming and prefix matching over titles, descriptions, tags and the extracted text. A **separate public index** only contains published items and the text of the *published* copy. Filters: tag, verification, type, date range. |
| Text extraction | `pdftotext` for PDFs, XML extraction for Office/OpenDocument, UTF-8 for text. **OCR** (tesseract) for scanned PDFs and images. |
| Transcription | **Hook, disabled by default.** Set `Atlas:TranscriptionCommand` (and `TranscriptionArgs`, with `{input}` for the media file) to any command that prints a transcript to stdout, for example whisper.cpp. The output is indexed. Nothing is bundled because speech models don't fit comfortably on a 1 GB VM. |
| Viewers | PDF as server-rendered page images (works on iPhone Safari), image viewer, video and audio players with HTTP range requests (seeking works), plain-text viewer. Download button on every item. |
| Thumbnails | PDF first page, image, video poster (ffmpeg). The public thumbnail is made from the public copy, so redactions show up there too. |
| Integrity | SHA-256 of the original is stored privately; the SHA-256 of the **published file** is shown publicly with a copy button so anyone can check their download. |
| Metadata stripping | Done on publish, on the public copy only. See *Security notes* for exactly what is and isn't covered. |
| Redaction | Draw boxes (touch or mouse) on image or PDF pages. The PDF is rasterized and rebuilt from page images with opaque black boxes burned in, so there is no text or vector data under a box. Redacted text is also excluded from the public search index. |
| Reports | A report/takedown form on every public item. Reports are stored in the database and shown in the admin area (no IP addresses stored; per-IP rate limit and a honeypot field). |
| Admin | Passcode login (cookie, rate limited), review queue, upload with progress, reports, settings. |
| Search engines | `noindex` is **on by default** (header, `robots.txt` disallow-all, no sitemap). Flip it in *Admin → Settings* when you are ready to be found. The admin area is never indexable. |
| Big files | Chunked, **resumable** uploads (8 MB pieces) streamed straight to disk, never held in memory. Default limit 6 GB per file; refuses uploads that would leave less than 2 GB free. |
| Mobile | Responsive layout, large tap targets, installable PWA manifest. |

## Quick start (development)

Requirements: .NET 8 SDK, and for the full pipeline `poppler-utils`, `qpdf`, `tesseract-ocr`, `ffmpeg`.

```bash
sudo apt-get install -y poppler-utils qpdf tesseract-ocr tesseract-ocr-eng ffmpeg   # Debian/Ubuntu
export ATLAS_PASSCODE="$(openssl rand -base64 24)"      # keep it out of shell history/files you commit
dotnet run --project src/Atlas.Web
# public site: http://localhost:5256   admin: http://localhost:5256/admin
dotnet test
```

Tests that need an external tool are skipped automatically when it isn't installed.

A ready-made GitHub Actions workflow is in `docs/github-actions-ci.yml`; copy it to `.github/workflows/ci.yml` to run the tests on every PR (it is kept here because the token used to create this repository could not write workflow files).

## Configuration

Settings come from `appsettings.json`, environment variables (`Atlas__Name`) or the command line.

| Setting | Default | Meaning |
| --- | --- | --- |
| `ATLAS_PASSCODE` (env) | none | Admin passcode. **Required**; if unset nobody can log in. Never put it in a file in the repo. |
| `Atlas:DataDir` | `data` | Database, originals, public copies, cache. Put it on a big disk. |
| `Atlas:MaxUploadBytes` | 6 GB | Largest accepted file. |
| `Atlas:MinFreeBytes` | 2 GB | Uploads are refused if less than this would remain free. |
| `Atlas:OcrMaxPages` | 60 | OCR page limit per item (OCR is slow on small machines). |
| `Atlas:OcrLanguage` | `eng` | Tesseract language(s), e.g. `eng+deu`. |
| `Atlas:TranscriptionCommand` / `TranscriptionArgs` | none | Transcription hook (see above). |
| `Atlas:LoginAttemptsPerMinute` | 5 | Per-IP login rate limit. |
| `Atlas:RequestsPerMinute` | 600 | Per-IP public request limit. |
| `Atlas:ReportsPerHour` | 5 | Per-IP report limit. |

Site name, intro text, footer/contact note and the search-engine switch are edited in *Admin → Settings*.

## Security notes

Read these before publishing anything sensitive.

* **Public vs private.** The public API and pages never include source notes, the original upload file name, upload time or status. Public download names come from the title. Tests assert this.
* **Source notes and originals are stored unencrypted on the server's disk** (SQLite database and `items/<id>/original.*`). Protect the machine, its backups and the passcode accordingly. Don't write anything in source notes you couldn't live with being disclosed.
* **Metadata stripping is best effort and applies to the public copy only:**
  * PDF: pages are copied into a new document with `qpdf`, which drops the document info dictionary, XMP packet, outlines and file-level metadata. Content inside pages (for example text or images that themselves carry identifying content) is untouched.
  * Images: re-encoded with ffmpeg and all metadata (EXIF, GPS, XMP, ICC, comments) dropped. Orientation is applied to the pixels. Re-encoding changes the bytes, which is why the public hash differs from the original's.
  * Audio/video: streams are copied without re-encoding; global and per-stream tags, chapters, data and subtitle streams are dropped. Metadata *inside* the encoded streams (for example SEI messages) is not rewritten.
  * Office/OpenDocument: document properties (author, last-modified-by, company, dates), custom properties, embedded thumbnail and tracked-change/comment author names (Word) are blanked. Images embedded in the document, comments in other formats and other custom XML are not rewritten. When in doubt, export to PDF and publish that.
  * Plain text: published as-is.
* **Redaction is real, but verify it.** Redacting a PDF rasterizes every page at 150 dpi and rebuilds the file from images. Review the published copy before announcing it. Redaction is not available for audio, video, text or Office files (convert documents to PDF first).
* **Hashes.** Anyone can verify a download against the SHA-256 on the item page. The hash is of the sanitized public file, not of your original.
* **Admin surface.** Cookie is `HttpOnly`, `SameSite=Strict`; state-changing requests with a foreign `Origin` are refused; login is rate limited; `X-Robots-Tag: noindex` is always sent for `/admin` and `/api/admin`. A strict Content-Security-Policy forbids inline scripts and styles. Uploaded files are served with their own content type from an allow-list plus `nosniff`; text is always `text/plain`, and Office files are always downloads.
* **Takedowns.** The public report form stores requests for the admin. Responding to them (and being comfortable with what you publish) is your responsibility.
* **No analytics, no third-party requests.**

## API (public, read-only)

| Endpoint | Description |
| --- | --- |
| `GET /api/docs?q=&tag=&verification=&kind=&from=&to=&limit=&offset=` | Published items, with search snippets (hits wrapped in `\u0001…\u0002`) |
| `GET /api/docs/{id}` | One published item |
| `GET /api/tags` | Tags of published items with counts |
| `GET /files/{id}` / `/files/{id}/download` | The public copy (inline with range support / as attachment) |
| `GET /thumbs/{id}.jpg`, `GET /pages/{id}/{n}.jpg` | Thumbnail and PDF page images |
| `POST /api/reports` | Submit a report |

Admin endpoints live under `/api/admin/*` and need the login cookie.

## Deployment

`deploy/` has a systemd unit, a Caddy template and an install script. The reference deployment is one small Azure VM
(Ubuntu 24.04, Caddy for automatic HTTPS) that also hosts other small apps; Atlas runs as its own `atlas` service on
`127.0.0.1:5100` with its own user, and its data lives on a separate data disk mounted at `/srv/atlas`.

```bash
dotnet publish src/Atlas.Web -c Release -o publish --no-self-contained
tar -C publish -czf atlas-publish.tar.gz .
# copy the tarball and deploy/ to the server, create /etc/atlas/atlas.env from deploy/atlas.env.example, then:
sudo ATLAS_HOST=archive.example.org ./deploy/install.sh /tmp/atlas-publish.tar.gz
```

Install the media tools on the server too: `apt-get install poppler-utils qpdf tesseract-ocr tesseract-ocr-eng ffmpeg`.
On a 1 GB machine keep OCR page limits modest and add swap.

## Project layout

```
src/Atlas.Web/        ASP.NET Core 8 app (Endpoints/, Services/, wwwroot/ front end: vanilla JS, no build step)
tests/Atlas.Tests/    xUnit tests (workflow, pipeline, units)
deploy/               systemd unit, Caddy template, install script, env example
docs/                 CI workflow to copy into .github/workflows/
```

## Contributing

Open an issue or pull request. Every change goes through a PR and is squash-merged. Please include tests; run `dotnet test`
before pushing. Don't add real people's names, real leaked material or any secrets to the repository, seed data or tests;
generate harmless fixtures instead (see `tests/Atlas.Tests/Fixtures.cs`).

## License

MIT
