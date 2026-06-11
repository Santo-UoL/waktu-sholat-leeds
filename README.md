# Waktu Sholat Leeds

Aplikasi desktop Windows (C# / WinForms, .NET 10) untuk menampilkan **jadwal waktu sholat** kota Leeds dengan tema Islami (hijau zamrud & emas).

![Tampilan](docs/screenshot.png)

## Fitur

- **Jadwal 5 waktu sholat** (Subuh, Zuhur, Asar, Maghrib, Isya) + jam iqamah, dibaca dari file CSV.
- **Tema Islami custom-paint**: bingkai emas, kaligrafi Arab, motif bulan sabit & bintang, highlight waktu sholat berikutnya.
- **Hitung mundur** menuju sholat berikutnya; **berkedip merah** saat ≤ 15 menit.
- **System tray**: berjalan di tray, ikon menampilkan sisa menit; klik untuk buka, minimize untuk sembunyi.
- **Overlay taskbar** adaptif di samping widget cuaca (mengikuti lebar widget otomatis).
- **Dwibahasa** Indonesia / Inggris (toggle ID/EN, tersimpan di `lang.txt`).
- Tombol **Cek Update** (cek versi online) dan **Uninstall**.

## Instalasi (pengguna)

Unduh installer `.msi` dari halaman [Releases](https://github.com/Santo-UoL/waktu-sholat-leeds/releases) — self-contained, tidak perlu install .NET.

> **Catatan:** browser mungkin menampilkan peringatan *"isn't commonly downloaded"* (SmartScreen). Ini normal untuk installer baru yang belum ditandatangani digital. Klik **…** → **Keep** → **Keep anyway**, dan saat menjalankan pilih **More info → Run anyway**.

## Menjalankan dari source

Butuh **.NET 10 SDK** (Windows).

```powershell
dotnet run --project "waktu sholat"
```

Atau buka `waktu sholat.slnx` di Visual Studio lalu tekan **F5**.

## Konfigurasi

Sebagian besar pengaturan ada sebagai konstanta di [`waktu sholat/Form1.cs`](waktu%20sholat/Form1.cs):

| Konstanta | Fungsi |
|-----------|--------|
| `CsvPath` | Lokasi file CSV jadwal sholat |
| `WarningMinutes` | Menit sebelum sholat mulai berkedip merah (default 15) |
| `OverlayLeftOffset` | Posisi awal/fallback overlay di taskbar |
| `UpdateCheckUrl` | URL cek versi (kosong = fitur cek update nonaktif) |

## Format CSV

Header: `Date,Weekday,Hijri,Fajr,Fajr_Iqama,Shuruq,Dhuhr,Dhuhr_Iqama,Asr,Asr_Iqama,Maghrib,Maghrib_Iqama,Isha,Isha_Iqama`

Kolom iqamah boleh berupa jam (`21:45`) atau teks keterangan (mis. `COMBINED WITH MAGHRIB`).

## Struktur

- `waktu sholat/Form1.cs` — UI custom-paint + logika utama (CSV, tray, overlay, bahasa, update/uninstall).
- `waktu sholat/TaskbarOverlay.cs` — jendela overlay melayang di taskbar.
- `waktu sholat/Program.cs` — entry point + penangkap error global.
- `waktu sholat/app.ico` — ikon aplikasi.
