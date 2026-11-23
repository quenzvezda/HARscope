# HARscope

MCP server lokal untuk menyelidiki file HAR (HTTP Archive) secara hemat token. Agent AI tidak pernah menerima isi
HAR utuh. Agent mengajukan query kecil secara bertahap (*progressive disclosure*: ringkasan → pencarian → satu entry →
potongan body), dan server hanya mengembalikan bagian yang diminta.

HARscope adalah *data access layer*, bukan analyzer berbasis LLM. Interpretasi tetap dikerjakan oleh agent yang memanggilnya.

Status: **Phase 1 (MVP)**: `har_list`, `har_open`, `har_overview`, `har_search`, `har_get_entry`.

## Arsitektur

```text
src/
  HarScope.Core/          library inti, tanpa dependensi MCP (bisa di-reuse untuk CLI)
    Security/PathGuard    validasi path: hanya file .har di bawah HAR_ROOT, tolak "..", path absolut, symlink/junction keluar root
    Security/Redactor     redaksi nilai header, cookie, dan query-string sensitif
    Parsing/HarParser     validasi HAR 1.2 + membangun metadata index
    Indexing/CaptureStore cache in-memory (LRU), capture ID, deteksi file berubah → reload
    Querying/             OverviewService, SearchService, EntryInspector, OutputGuard (batas output)
  HarScope.Mcp/           host MCP stdio (adapter tipis ke Core)
tests/
  HarScope.Core.Tests/    unit test Core (fixture HAR sintetis)
  HarScope.Mcp.Tests/     test tool + test end-to-end dengan client MCP resmi lewat stdio
samples/checkout-flow.har HAR sintetis: login → profile → search → cart → checkout → payment gagal
```

Alur data:

```text
file .har (di bawah HAR_ROOT)
  → PathGuard (canonical path, cek link)
  → HarParser: JsonDocument + metadata index per entry (ID, waktu, method, host, path, status, durasi, mime,
    ketersediaan body, header korelasi)
  → CaptureStore (maks N capture di memori, reload otomatis bila file berubah)
  → Overview / Search / EntryInspector  (body dibaca lazy dari JsonDocument, hanya saat diminta)
  → Redactor + OutputGuard → JSON ringkas ke agent
```

### Capture ID dan entry ID

- `captureId` = slug dari path relatif file: huruf kecil, karakter selain huruf/angka diganti `-`, tanpa `.har`.
  `Checkout Gagal 23-11.har` → `checkout-gagal-23-11`; `team/login.har` → `team-login`. Bila dua file menghasilkan slug yang sama,
  file yang dibuka belakangan mendapat suffix `-2`.
- `entryId` = `<captureId>:entry-<n>`. `n` adalah posisi asli (mulai dari 1) di array `log.entries`.
- Setiap hasil menyertakan `revision` (hash pendek isi file). Bila file berubah di disk, capture di-reload dan agent
  menerima warning bahwa entry ID lama bisa menunjuk ke request yang berbeda.

## Prasyarat

- .NET SDK 10 (diuji dengan 10.0.402)
- Tidak perlu database, layanan cloud, atau akses internet saat runtime.

## Build, test, publish

```powershell
dotnet build
```

```powershell
dotnet test
```

```powershell
dotnet publish src/HarScope.Mcp -c Release -o publish
```

Hasil publish: `publish\HarScope.Mcp.exe`. Exe ini *framework-dependent*, jadi mesin target butuh .NET 10 runtime.
Gunakan exe ini di config MCP. Jangan pakai `dotnet run` karena perintah itu mem-build ulang setiap kali client start (lambat)
dan output build-nya bisa mengotori stdout.

## Setup MCP client

Semua contoh memakai stdio. Ganti `D:\\network-logs` dengan folder tempat Anda menyimpan file `.har`.

**Claude Code (CLI):**

```powershell
claude mcp add harscope --env HAR_ROOT=D:\network-logs -- D:\rama-project\HARscope\publish\HarScope.Mcp.exe
```

**`.mcp.json` (Claude Code, scope project):**

```json
{
  "mcpServers": {
    "harscope": {
      "type": "stdio",
      "command": "D:\\rama-project\\HARscope\\publish\\HarScope.Mcp.exe",
      "env": { "HAR_ROOT": "D:\\network-logs" }
    }
  }
}
```

**Claude Desktop (`claude_desktop_config.json`), Cursor, dan client lain** memakai format `mcpServers` yang sama
(`command` + `env`).

**Mencoba dengan sample:** arahkan `HAR_ROOT` ke folder `samples` di repo ini, lalu minta agent membuka `checkout-flow.har`.

### Mengekspor HAR dari browser

Chrome/Edge: DevTools → tab **Network** → reproduksi alurnya → klik kanan daftar request → **Save all as HAR with content**
(atau ikon download "Export HAR"). Simpan file ke folder `HAR_ROOT`. Chrome versi baru menyediakan pilihan ekspor HAR
yang sudah disanitasi (tanpa cookie/authorization) dan yang lengkap. Keduanya bisa dibaca HARscope.

## Konfigurasi

Konfigurasi dibaca dari environment variable. Alternatifnya, argumen command line (`--HAR_ROOT D:\logs`) atau
`appsettings.json` di folder exe.

| Variabel | Default | Keterangan |
|---|---|---|
| `HAR_ROOT` | (wajib) | Folder file HAR. Tidak ada file di luar folder ini yang bisa dibaca. |
| `HARSCOPE_MAX_FILE_MB` | 256 | File yang lebih besar ditolak (seluruh file dimuat ke memori). |
| `HARSCOPE_MAX_OUTPUT_CHARS` | 24000 | Batas ukuran satu hasil tool (≈ 6k token). |
| `HARSCOPE_MAX_OPEN_CAPTURES` | 4 | Jumlah capture di memori; yang paling lama tidak dipakai akan di-evict lalu di-reload saat diminta lagi. |
| `HARSCOPE_DEFAULT_BODY_CHARS` | 4000 | Panjang potongan body default di `har_get_entry`. |
| `HARSCOPE_REDACT` | `on` | `off` mematikan semua redaksi. |
| `HARSCOPE_REDACT_HEADERS` | – | Nama header tambahan yang diredaksi, pisahkan dengan koma. |
| `HARSCOPE_REDACT_QUERY` | – | Nama query param tambahan yang diredaksi, pisahkan dengan koma. |

Batas lain (tetap di kode, `HarScopeOptions`): page pencarian default 20 dan maksimum 100; body maksimum 20.000 karakter per
potongan; satu nilai header/cookie/query maksimum 1.000 karakter; `har_list` maksimum 500 file.

## Tool

| Tool | Fungsi | Catatan output |
|---|---|---|
| `har_list` | Daftar file `.har` di `HAR_ROOT` (terbaru dulu) | path relatif, ukuran, mtime, `captureId` bila sudah dibuka |
| `har_open(path)` | Load file dan jadikan capture aktif | `captureId`, `revision`, jumlah entry, jumlah gagal, rentang waktu |
| `har_overview(captureId?, slowest=5)` | Statistik level 1 | distribusi method/status/host/mime/resource type, endpoint unik, request terlambat. Tanpa body. |
| `har_search(...)` | Filter + sort + paging | satu baris ringkas per entry: `id`, `t`, `method`, `host`, `path`, `status`, `ms`, `mime`, `flags` |
| `har_get_entry(entryId, sections?, bodyOffset?, bodyMaxChars?)` | Inspeksi satu entry | default hanya `summary`; section lain harus diminta |

Filter `har_search`: `urlContains`, `method`, `status`, `statusMin`/`statusMax`, `host`, `pathContains`, `mimeType`,
`resourceType`, `minDurationMs`/`maxDurationMs`, `from`/`to` (ISO-8601), `requestHeader`/`responseHeader` (nama header),
`correlationId`, `failedOnly`. Sort: `time` (default), `duration`, `status`. Metadata hasil: `totalMatches`, `returnedCount`,
`hasMore`, `nextOffset`, `truncated`, `warnings`.

Flags pada baris hasil: `failed` (status 0, ≥ 400, atau network error), `netError:<teks>`, `redirect`, `reqBody`,
`resBody` / `resBody:base64`, `noResBody`, `correlated` (punya header trace/korelasi).

Section `har_get_entry`: `requestHeaders`, `responseHeaders`, `headers` (keduanya), `queryParams`, `requestBody`,
`responseBody`, `timings`, `cookies`.
- Body dikembalikan per potongan: `totalChars`, `offset`, `returnedChars`, `truncated`, `nextOffset`. Untuk lanjut,
  panggil lagi dengan `bodyOffset = nextOffset`.
- Body Base64 yang berupa teks (mis. JSON) di-decode. Body biner (gambar, font, dll.) hanya dikembalikan metadatanya.
- Body yang tidak ada di HAR dilaporkan `available: false` beserta alasannya, tanpa diisi tebakan.
- Timing bernilai `-1` (sentinel HAR untuk "tidak berlaku") dilaporkan sebagai `null`.

## Contoh pertanyaan ke agent

- "Buka HAR terbaru dan rangkum request yang gagal."
  → `har_list` → `har_open` → `har_overview` → `har_search(failedOnly=true)`
- "Cari semua request ke `/api/checkout`." → `har_search(pathContains="/api/checkout")`
- "Kenapa pembayaran gagal?"
  → `har_search(failedOnly=true)` → `har_get_entry(id, ["requestBody","responseBody"])` →
  `har_search(correlationId="corr-77")` untuk request lain dengan correlation ID yang sama
- "Request apa yang paling lambat saat login?" → `har_search(pathContains="login", sort="duration", descending=true)`
- "Tampilkan header request yang gagal ini." → `har_get_entry(id, ["requestHeaders"])`

## Privasi dan keamanan

- Server hanya berjalan lokal: tidak ada telemetry, upload, atau request jaringan keluar. File HAR dibaca read-only.
- Akses file dibatasi ke `HAR_ROOT`. Path absolut, `..`, alternate data stream (`:`), file non-`.har`, serta
  symlink/junction yang mengarah ke luar root ditolak. `har_list` tidak mengikuti link.
- **Redaksi (default aktif)** berlaku untuk nilai header (`Authorization`, `Cookie`, `Set-Cookie`, `X-Api-Key`, nama yang
  mengandung `token`/`secret`/`password`/`session`, dll.), semua nilai cookie, query param sensitif (`access_token`,
  `api_key`, `code`, `signature`, dll.), dan nilai yang berbentuk `Bearer …`/`Basic …`/JWT.
  Format hasilnya `[REDACTED len=142 fp=a1b2c3d4]`. `fp` adalah HMAC dengan kunci acak per proses: dua nilai yang sama
  menghasilkan `fp` yang sama dalam satu sesi server, tetapi tidak bisa dicocokkan di luar sesi itu.
- **Body request/response tidak diredaksi.** Password atau token di dalam body (mis. body login) bisa terlihat oleh agent
  bila agent meminta section body. Jangan memakai HAR berisi kredensial produksi yang sensitif.
- Log hanya ditulis ke stderr (stdout khusus untuk protokol MCP) dan tidak pernah berisi header atau body.
- Isi HAR diperlakukan sebagai data tidak tepercaya. Deskripsi tool mengingatkan agent bahwa teks di dalam HAR bukan instruksi.
- Ekspor HAR yang "sudah disanitasi" oleh browser tetap bisa berisi data sensitif di body atau URL.

## Keterbatasan (Phase 1)

- **Memori**: seluruh file dimuat ke memori sebagai `JsonDocument`. Perkiraan pemakaian ≈ ukuran file + metadata parser
  (~12 byte per token JSON) per capture yang terbuka. Karena itu ada batas `HARSCOPE_MAX_FILE_MB` dan LRU
  `HARSCOPE_MAX_OPEN_CAPTURES`. Belum ada streaming parser atau index persisten (SQLite).
- Belum ada query JSON di dalam body (`har_query_body`), pencarian berdasarkan field body, trace flow, atau perbandingan
  entry. Semuanya direncanakan untuk Phase 2.
- `har_search` belum mencari di isi body.
- Filter waktu memakai `startedDateTime` dari HAR. Entry tanpa timestamp valid selalu diurutkan paling akhir.
- Entry ID terikat ke isi file. Bila file di-overwrite dengan capture lain, ID lama tidak lagi bermakna (ditandai lewat
  warning dan `revision`).
- Tidak ada replay request. Itu di luar cakupan MVP dan butuh desain keamanan tersendiri.
