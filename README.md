# HARscope

MCP server lokal untuk menyelidiki file HAR (HTTP Archive) secara hemat token. Agent AI tidak pernah menerima isi
HAR utuh. Agent mengajukan query kecil secara bertahap (*progressive disclosure*: ringkasan → timeline → pencarian →
satu entry → bagian body), dan server hanya mengembalikan bagian yang diminta.

HARscope adalah *data access layer*, bukan analyzer berbasis LLM. Interpretasi tetap dikerjakan oleh agent yang memanggilnya.

Versi **0.3.0**, 10 tool:

| Kelompok | Tool |
|---|---|
| File | `har_list`, `har_open` |
| Ringkasan dan timeline | `har_overview`, `har_trace_flow` |
| Pencarian dan inspeksi | `har_search`, `har_get_entry`, `har_query_body` |
| Perbandingan | `har_compare_entries`, `har_compare_captures` |
| Reproduksi | `har_repro` (template saja, tidak ada request yang dikirim) |

## Arsitektur

```text
src/
  HarScope.Core/          library inti, tanpa dependensi MCP (bisa di-reuse untuk CLI)
    Security/PathGuard    validasi path: hanya file .har di bawah HAR_ROOT, tolak "..", path absolut, symlink/junction keluar root
    Security/Redactor     redaksi nilai header, cookie, dan query-string sensitif
    Parsing/HarParser     validasi HAR 1.2 + membangun metadata index
    Indexing/CaptureStore cache capture in-memory (LRU), capture ID, deteksi file berubah → reload
    Querying/             OverviewService, SearchService, EntryInspector, BodyReader (decode body),
                          JsonPath (subset JSONPath), WhereClause (filter baris), BodyQueryService,
                          JsonBodyCache (LRU body JSON yang sudah di-parse), OutputGuard (batas output)
    Analysis/             FlowService (timeline), CompareService + JsonDiff (diff), ReproService (template reproduksi)
  HarScope.Mcp/           host MCP stdio (adapter tipis ke Core) + ServerGuide (instruksi untuk client)
tests/
  HarScope.Core.Tests/    unit test Core (fixture HAR sintetis)
  HarScope.Mcp.Tests/     test tool + test end-to-end dengan client MCP resmi lewat stdio
scripts/
  install.ps1             test → publish ke staging → ganti file yang terkunci → verifikasi
  bench/Bench.cs          benchmark HAR besar sintetis (file-based app .NET 10)
samples/checkout-flow.har HAR sintetis: login → profile → search → cart → checkout → payment gagal
```

Alur data:

```text
file .har (di bawah HAR_ROOT)
  → PathGuard (canonical path, cek link)
  → HarParser: JsonDocument + metadata index per entry (ID, waktu, method, host, path, status, durasi, mime,
    ketersediaan body, header korelasi)
  → CaptureStore (maks N capture di memori, reload otomatis bila file berubah)
  → Overview / Flow / Search / EntryInspector / BodyQuery / Compare / Repro
    (body di-decode dan di-parse hanya saat diminta, lalu disimpan di JsonBodyCache)
  → Redactor + OutputGuard → JSON ringkas ke agent
```

### Capture ID dan entry ID

- `captureId` = slug dari path relatif file: huruf kecil, karakter selain huruf/angka diganti `-`, tanpa `.har`.
  `Checkout Gagal 23-11.har` → `checkout-gagal-23-11`; `team/login.har` → `team-login`. Bila dua file menghasilkan slug yang sama,
  file yang dibuka belakangan mendapat suffix `-2`.
- `entryId` = `<captureId>:entry-<n>`. `n` adalah posisi asli (mulai dari 1) di array `log.entries`.
- Setiap hasil menyertakan `revision` (hash pendek isi file). Bila file berubah di disk, capture di-reload dan agent
  menerima warning bahwa entry ID lama bisa menunjuk ke request yang berbeda.

### Cara server berjalan

Transport-nya stdio. Setiap sesi MCP client (mis. satu sesi Claude Code) menjalankan prosesnya sendiri sebagai proses anak.
Proses itu tetap hidup selama sesi terbuka dan menunggu pesan di stdin tanpa memakai CPU. Server tidak membuka port.
Akibatnya, cache HAR dan kunci fingerprint redaksi berlaku per sesi, dan file exe terkunci selama ada sesi yang memakainya
(lihat `scripts/install.ps1`).

## Prasyarat

- .NET SDK 10 (diuji dengan 10.0.402)
- Tidak perlu database, layanan cloud, atau akses internet saat runtime.

## Build, test, install

```powershell
dotnet build
```

```powershell
dotnet test
```

Install atau update ke `%USERPROFILE%\.mcp-servers\harscope`. Skrip ini menjalankan test, publish ke staging, mematikan
proses HARscope yang mengunci file, menyalin, lalu memverifikasi hash:

```powershell
.\scripts\install.ps1
```

Pemasangan pertama sekaligus mendaftarkan ke Claude Code (scope user):

```powershell
.\scripts\install.ps1 -Register -HarRoot D:\network-logs
```

Proses HARscope yang dimatikan saat update akan dijalankan lagi oleh Claude Code. Daftar tool yang berubah baru terlihat
di sesi baru.

Exe hasil publish bersifat *framework-dependent*, jadi mesin target butuh .NET 10 runtime. Jangan pakai `dotnet run` di
config MCP, karena perintah itu mem-build ulang setiap kali client start (lambat) dan output build-nya bisa mengotori stdout.

## Setup MCP client manual

Semua contoh memakai stdio. Ganti `D:\\network-logs` dengan folder tempat Anda menyimpan file `.har`.

**Claude Code (CLI):**

```powershell
claude mcp add harscope --scope user --env HAR_ROOT=D:\network-logs -- C:\Users\<user>\.mcp-servers\harscope\HarScope.Mcp.exe
```

**`.mcp.json` (Claude Code, scope project):**

```json
{
  "mcpServers": {
    "harscope": {
      "type": "stdio",
      "command": "C:\\Users\\<user>\\.mcp-servers\\harscope\\HarScope.Mcp.exe",
      "env": { "HAR_ROOT": "D:\\network-logs" }
    }
  }
}
```

**Claude Desktop (`claude_desktop_config.json`), Cursor, dan client lain** memakai format `mcpServers` yang sama
(`command` + `env`).

Saat terhubung, server mengirim **instructions** (panduan singkat untuk model): kapan HARscope dipakai, urutan tool
yang disarankan, dan larangan membaca file `.har` secara langsung. Teksnya ada di
[ServerGuide.cs](src/HarScope.Mcp/ServerGuide.cs).

**Mencoba dengan sample:** arahkan `HAR_ROOT` ke folder `samples` di repo ini, lalu minta agent membuka `checkout-flow.har`.

### Mengekspor HAR dari browser

Chrome/Edge: DevTools → tab **Network** → centang **Preserve log** → jalankan alurnya → klik kanan daftar request →
**Save all as HAR with content**. Simpan file ke folder `HAR_ROOT`. Chrome versi baru menyediakan pilihan ekspor HAR
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

Batas lain (tetap di kode): page pencarian default 20 dan maksimum 100; body maksimum 20.000 karakter per potongan;
satu nilai header/cookie/query maksimum 1.000 karakter; `har_list` maksimum 500 file; cache body JSON 64 entry / 64 MB.

## Tool

| Tool | Fungsi | Catatan output |
|---|---|---|
| `har_list` | Daftar file `.har` di `HAR_ROOT` (terbaru dulu) | path relatif, ukuran, mtime, `captureId` bila sudah dibuka |
| `har_open(path)` | Load file dan jadikan capture aktif | `captureId`, `revision`, jumlah entry, jumlah gagal, rentang waktu |
| `har_overview(captureId?, slowest=5)` | Statistik level 1 | distribusi method/status/host/mime/resource type, endpoint unik, request terlambat. Tanpa body. |
| `har_trace_flow(...)` | Timeline per kelompok aksi | grup berdasarkan jeda waktu, call per grup, kegagalan beruntun, link pasti |
| `har_search(...)` | Filter + sort + paging | satu baris ringkas per entry: `id`, `t`, `method`, `host`, `path`, `status`, `ms`, `mime`, `flags` |
| `har_get_entry(entryId, sections?, bodyOffset?, bodyMaxChars?)` | Inspeksi satu entry | default hanya `summary`; section lain harus diminta |
| `har_query_body(entryId, path?, ...)` | Bagian tertentu dari body JSON | nilai + path konkret, `fields`, `where`, struktur, atau agregat |
| `har_compare_entries(entryIdA, entryIdB, ...)` | Diff dua entry | status, query, header, body JSON per path |
| `har_compare_captures(captureA, captureB, ...)` | Diff call yang sama di dua HAR | pasangan berbeda/sama, call yang hanya ada di satu sisi |
| `har_repro(entryIds, format?, allHeaders?)` | Template reproduksi tersanitasi | `.http` atau cURL, `variables`, `dependencies` |

### `har_search`

Filter: `urlContains`, `method`, `status`, `statusMin`/`statusMax`, `host`, `pathContains`, `mimeType`, `resourceType`,
`minDurationMs`/`maxDurationMs`, `from`/`to` (ISO-8601), `requestHeader`/`responseHeader` (nama header), `correlationId`,
`bodyContains` (teks di body yang sudah di-decode; `bodySide` = `request`/`response`/keduanya), `failedOnly`,
`includeDataUrls`. Entri `data:` (gambar/font inline) disembunyikan secara default dan jumlahnya disebut di `warnings`.
Sort: `time` (default), `duration`, `status`. Metadata hasil: `totalMatches`, `returnedCount`, `hasMore`, `nextOffset`,
`truncated`, `warnings`.

Flags pada baris hasil: `failed` (status 0, ≥ 400, atau network error), `netError:<teks>`, `redirect`, `reqBody`,
`resBody` / `resBody:base64`, `noResBody`, `correlated` (punya header trace/korelasi).

### `har_get_entry`

Section: `requestHeaders`, `responseHeaders`, `headers` (keduanya), `queryParams`, `requestBody`, `responseBody`,
`timings`, `cookies`.
- Body dikembalikan per potongan: `totalChars`, `offset`, `returnedChars`, `truncated`, `nextOffset`. Bila body JSON lebih
  besar dari satu potongan, hasilnya menyarankan `har_query_body`.
- Body Base64 yang berupa teks (mis. JSON) di-decode. Body biner (gambar, font, dll.) hanya dikembalikan metadatanya.
- Body yang tidak ada di HAR dilaporkan `available: false` beserta alasannya, tanpa diisi tebakan.
- Timing bernilai `-1` (sentinel HAR untuk "tidak berlaku") dilaporkan sebagai `null`.

### `har_query_body`

Untuk body JSON besar, agent tidak perlu membaca teks per potongan. Cukup ambil bagian yang relevan.

**Sintaks path** (subset JSONPath, bukan JSONPath penuh):

| Sintaks | Arti |
|---|---|
| `$` | root (boleh dihilangkan: `data.items` = `$.data.items`) |
| `.name`, `['name']` | akses property (pakai bracket untuk nama berspasi/bersimbol) |
| `[n]`, `[-1]` | index array; negatif dihitung dari belakang |
| `[*]`, `.*` | semua elemen array / semua nilai property object |
| `[start:end]` | slice array (end eksklusif) |

Tidak didukung: recursive descent `..`, filter `[?()]`, union `[a,b]`, slice dengan step. Path seperti ini ditolak dengan pesan
yang menyebutkan sintaks yang didukung. Untuk menyaring baris, gunakan `where`.

**Mode dan opsi:**

- `mode="values"` (default): setiap match dikembalikan bersama path konkretnya (mis. `$.data[3].uic`), dengan paging
  `offset`/`limit` (default 20, maks 200). Nilai object/array yang lebih dari 2.000 karakter diganti ringkasan
  (`_summary`, `keys`/`length`).
- `fields=["errorCode","detail.message"]`: proyeksikan field relatif dari setiap match.
- `where=["<field> <op> [nilai]", ...]`: saring match sebelum proyeksi/agregat; beberapa kondisi digabung dengan AND.
  Operator: `==`, `!=`, `>`, `>=`, `<`, `<=`, `contains`, `notContains`, `startsWith` (teks, case-insensitive),
  `exists`, `notExists`. Nilai berupa angka, `true`/`false`/`null`, `'teks berspasi'`, atau teks tanpa kutip. `@` berarti
  match itu sendiri. Field yang tidak ada tidak pernah lolos `==`/`<`/`>`/`contains`/`exists`, tetapi lolos `!=` dan
  `notContains`. Hasil menyertakan `scanned` (jumlah sebelum filter) dan `matched` (sesudah filter).
- `mode="structure"`: bentuk body tanpa nilai (key, tipe, panjang array, gabungan key dari maksimal 25 elemen sampel).
- `aggregate`: `count`, `sum`, `min`, `max`, `avg`, `distinct`, ditambah `valueField` (field yang dihitung) dan `groupBy`
  (kelompokkan per field). Maksimal 100 grup, diurutkan dari nilai terbesar. Nilai non-angka dilewati dan dihitung di `skipped`.

Contoh (respons dashboard `{"data":{"data":[{"uic":..,"risiko":..,"jumlah":..}, ...]}}`):

| Pertanyaan | Panggilan |
|---|---|
| Bentuk body? | `har_query_body(id, mode="structure")` |
| Ada berapa baris? | `har_query_body(id, path="$.data.data[*]", aggregate="count")` |
| Auditee apa saja dan berapa barisnya? | `har_query_body(id, path="$.data.data[*].uic", aggregate="distinct")` |
| Total temuan per auditee? | `har_query_body(id, path="$.data.data[*]", aggregate="sum", valueField="jumlah", groupBy="uic")` |
| Hanya `uic` dan `jumlah` per baris? | `har_query_body(id, path="$.data.data[*]", fields=["uic","jumlah"])` |
| Apakah semua baris milik debitur X? | `har_query_body(id, path="$.data.data[*]", where=["namaDebitur notContains X"])`; `matched` 0 berarti semua baris memuat X |
| Isi `auditeeIds` di request? | `har_query_body(id, source="request", path="$.auditeeIds")` |

### `har_trace_flow`

Timeline kronologis yang dikelompokkan menjadi grup. Grup baru dimulai bila sebuah request berjalan lebih dari `gapMs`
(default 800 ms) setelah semua request sebelumnya selesai. Setiap grup biasanya sama dengan satu aksi user (klik, load
halaman), tetapi ini **heuristik**: kedekatan waktu tidak membuktikan dua request berasal dari aksi yang sama.

Isi hasil: per grup `start`/`end`, `durationMs`, `gapBeforeMs`, jumlah call/gagal/lambat, `firstFailure`, dan daftar call
(offset ms dari awal grup, status, durasi, flags termasuk `slow`; maks 25 per grup). Hubungan yang didukung data dilaporkan
terpisah di `confirmedLinks` (nilai header trace/korelasi yang sama, target redirect), sedangkan `failureStreaks` berisi
kegagalan beruntun. Aset statis (gambar, font, CSS, JS) dan URL `data:` disembunyikan secara default (`includeStatic`).
Opsi lain: `from`/`to`, `pathContains`, `slowMs`, paging grup `offset`/`limit`.

### `har_compare_entries`

Diff dua entry dari capture yang sama atau berbeda:
- `differences` (ringkasan satu baris), status, query param, dan header request/response.
- Header yang berubah di setiap request (`date`, `etag`, `x-request-id`, `traceparent`, ...) diabaikan kecuali
  `includeVolatileHeaders`; jumlahnya ada di `ignoredHeaderChanges`.
- Body JSON di-diff per path (`added`, `removed`, `changed`, `typeChanged`; array per index, perubahan panjang dilaporkan
  sebagai `$.x.length`). Body teks lain melaporkan posisi pertama yang berbeda beserta potongan konteks.
- `ignorePaths` (mis. `["$.meta.timestamp", "$.data[*].updatedAt"]`) melewati field yang memang selalu berubah.
- Nilai header/query sensitif tetap diredaksi; `fp` yang sama berarti nilainya sama.

### `har_compare_captures`

Membandingkan call yang sama di dua HAR yang sudah dibuka, mis. **sebelum dan sesudah perbaikan**:
- Call dipasangkan berdasarkan method + path + query string yang diurutkan (host diabaikan, jadi DEV vs staging tetap
  cocok), sesuai urutan kemunculan.
- Per pasangan: status A/B, durasi, request body sama/berbeda, response body sama/berbeda (JSON dibandingkan per
  struktur) dan jumlah diff. Pasangan yang berbeda diurutkan paling atas: perubahan status dulu, lalu respons, lalu request.
- `onlyInA` / `onlyInB` berisi call yang tidak punya pasangan.
- Opsi: `pathContains`, `method`, `ignorePaths`, `differentOnly`, `includeStatic`, paging.
- Untuk melihat detail satu pasangan, lanjutkan dengan `har_compare_entries`.

### `har_repro`

Template reproduksi untuk maksimal 10 entry, diurutkan kronologis. **Tidak ada request yang dikirim.**
- Format `http` (default; untuk REST Client VS Code / IntelliJ HTTP Client, tidak bergantung shell) atau `curl` (quoting bash;
  di PowerShell gunakan `curl.exe` dan sesuaikan quoting).
- Header, cookie, dan query param sensitif, serta field body JSON/form yang terlihat rahasia (`password`, `token`, `secret`,
  `otp`, `pin`, ...), diganti `{{placeholder}}` dan didaftarkan di `variables`.
- Header yang dikelola browser atau selalu berubah dibuang. Mode minimal hanya menyimpan `content-type`, `accept`,
  `accept-language`, header `x-*` non-volatile, dan placeholder rahasia. Pakai `allHeaders=true` untuk semua header non-rahasia.
- `dependencies`: nilai di request (query, header `x-*`/auth, field body) yang pertama kali muncul di respons sebelumnya,
  mis. token dari login atau ID dari pembuatan order. Nilai rahasia ditampilkan dalam bentuk teredaksi.
- Request yang direkam belum tentu bisa diulang: sesi, CSRF token, cookie, atau state server mungkin sudah berubah.

## Contoh pertanyaan ke agent

- "Buka HAR terbaru dan rangkum request yang gagal."
  → `har_list` → `har_open` → `har_overview` → `har_search(failedOnly=true)`
- "Apa saja yang terjadi saat saya klik Simpan?" → `har_trace_flow` → lihat grup yang berisi call simpan
- "Kenapa pembayaran gagal?"
  → `har_search(failedOnly=true)` → `har_query_body(id, fields=["errorCode","message"])` →
  `har_search(correlationId="corr-77")` untuk request lain dengan correlation ID yang sama
- "Bandingkan checkout yang sukses dengan yang gagal." → `har_compare_entries(idSukses, idGagal)`
- "Apakah respons berubah setelah perbaikan?" → `har_open(sebelum.har)`, `har_open(sesudah.har)` →
  `har_compare_captures(a, b, differentOnly=true)` → `har_compare_entries` untuk pasangan yang berbeda
- "Pastikan semua baris di respons ini milik auditee X." → `har_query_body(id, path="$.data[*]", where=["uic != X"])`
- "Request mana yang mengirim ID auditee X?" → `har_search(bodyContains="X", bodySide="request")`
- "Buatkan request untuk mengulang login lalu ambil profil." → `har_repro([idLogin, idProfil])`

## Performa

Diukur dengan [scripts/bench/Bench.cs](scripts/bench/Bench.cs) pada HAR sintetis (respons JSON 6–18 KB per entry):

| Ukuran HAR | Entry | Load (baca + parse + index) | Memori (heap) | `bodyContains` semua body | Query lain |
|---|---|---|---|---|---|
| 150 MB | 9.911 | 1,2 s | 175 MB (1,17×) | 0,29 s | ≤ 0,11 s |
| 400 MB | 26.278 | 2,2 s | 457 MB (1,14×) | 0,79 s | ≤ 0,2 s |

`har_query_body` pada body yang sudah di-cache memakan beberapa ms (parse pertama ±25–45 ms). Memori bersifat per proses,
jadi setiap sesi yang membuka HAR besar memegang salinannya sendiri. Ini alasan batas default 256 MB.

```powershell
dotnet run scripts/bench/Bench.cs -- $env:TEMP\harscope-bench 150
```

## Privasi dan keamanan

- Server hanya berjalan lokal: tidak ada telemetry, upload, atau request jaringan keluar. File HAR dibaca read-only.
  `har_repro` hanya membuat teks dan tidak pernah mengirim request.
- Akses file dibatasi ke `HAR_ROOT`. Path absolut, `..`, alternate data stream (`:`), file non-`.har`, serta
  symlink/junction yang mengarah ke luar root ditolak. `har_list` tidak mengikuti link.
- **Redaksi (default aktif)** berlaku untuk nilai header (`Authorization`, `Cookie`, `Set-Cookie`, `X-Api-Key`, nama yang
  mengandung `token`/`secret`/`password`/`session`, dll.), semua nilai cookie, query param sensitif (`access_token`,
  `api_key`, `code`, `signature`, dll.), dan nilai yang berbentuk `Bearer …`/`Basic …`/JWT.
  Format hasilnya `[REDACTED len=142 fp=a1b2c3d4]`. `fp` adalah HMAC dengan kunci acak per proses: dua nilai yang sama
  menghasilkan `fp` yang sama dalam satu sesi server, tetapi tidak bisa dicocokkan di luar sesi itu.
- **Body request/response tidak diredaksi** di `har_get_entry`, `har_query_body`, dan `har_compare_*`. Password atau token
  di dalam body (mis. body login) bisa terlihat oleh agent. Pengecualiannya `har_repro`, yang mengganti field body yang
  terlihat rahasia dengan placeholder. Jangan memakai HAR berisi kredensial produksi yang sensitif.
- Log hanya ditulis ke stderr (stdout khusus untuk protokol MCP) dan tidak pernah berisi header atau body.
- Isi HAR diperlakukan sebagai data tidak tepercaya. Deskripsi tool dan server instructions mengingatkan agent bahwa teks
  di dalam HAR bukan instruksi.
- Ekspor HAR yang "sudah disanitasi" oleh browser tetap bisa berisi data sensitif di body atau URL.

## Keterbatasan

- **Memori**: seluruh file dimuat ke memori (±1,15× ukuran file per capture yang terbuka, per sesi). Belum ada streaming
  parser atau index persisten (SQLite).
- `bodyContains` adalah pencocokan teks biasa (case-insensitive), bukan pencarian per field JSON. Untuk menyaring
  berdasarkan nilai field, cari kandidat dengan `bodyContains`, lalu periksa dengan `har_query_body` + `where`.
- `har_query_body`, diff body, dan dependency di `har_repro` hanya untuk body JSON. Body XML, HTML, dan form dibaca
  sebagai teks lewat `har_get_entry` (diff teks hanya melaporkan posisi pertama yang berbeda).
- Diff array JSON per index: elemen yang hanya bergeser urutan terlihat sebagai banyak perubahan.
- Pemasangan call di `har_compare_captures` mengandalkan method + path + query. Call ke endpoint yang sama dengan
  parameter yang selalu berubah (mis. cache buster `?_=123`) tidak akan berpasangan.
- Pengelompokan `har_trace_flow` murni berdasarkan waktu; request background (polling, analytics) bisa ikut masuk ke grup.
- Filter waktu memakai `startedDateTime` dari HAR. Entry tanpa timestamp valid diurutkan paling akhir dan tidak tampil di
  `har_trace_flow`.
- Entry ID terikat ke isi file. Bila file di-overwrite dengan capture lain, ID lama tidak lagi bermakna (ditandai lewat
  warning dan `revision`).
- Tidak ada replay request. Itu butuh desain keamanan tersendiri (target yang diizinkan, konfirmasi eksplisit).
