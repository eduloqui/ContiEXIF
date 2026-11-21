# ContiExif

A fast, modern photo catalog and EXIF metadata manager for Windows built with .NET 8 and WinUI 3.

ContiExif lets you scan photo directories, index image metadata into a local database, and filter your photo collection effortlessly by camera body, lens, exposure settings (focal length, aperture, shutter speed, ISO), and date/time.

---

## ✨ Features

- **Blazing Fast Scanning & Indexing**: Scans image folders and indexes metadata into an embedded SQLite database.
- **Deep EXIF Metadata Filtering**:
  - **Gear**: Camera make/model and lens.
  - **Exposure**: Focal length, aperture (F-number), shutter speed, and ISO ranges.
  - **Time**: Filter dynamically by Year, Month, Day, and Hour.
- **Thumbnail Cache**: Instant thumbnail browsing with smooth pagination (60, 120, 240, 480 items per page).
- **Flexible Sorting**: Sort photos by Date Taken, Focal Length, Aperture, Shutter Speed, ISO, or Shuffle/Random.
- **Modern Windows Native UI**: Built using Windows App SDK / WinUI 3 with fluent design elements and responsive sliders.
- **Portable & Self-Contained**: No external database engines or heavy frameworks required.

---

## 📥 Download & Installation

### Option 1: Pre-built Binary (Recommended)
1. Go to the [Releases](https://github.com/YOUR_USERNAME/ContiExif/releases) page.
2. Download the latest `ContiExif-vX.X.X-win-x64.zip`.
3. Extract the ZIP archive anywhere on your PC.
4. Run `ContiExif.exe`.

> **Note on Windows SmartScreen**: Because this application is an independent open-source release without an expensive commercial code-signing certificate, Windows SmartScreen may show a popup saying *"Windows protected your PC"*. To proceed, click **More info** and then click **Run anyway**.

---

## 🛠️ Building from Source

### Prerequisites
- Windows 10 (version 19041+) or Windows 11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 (recommended, with **.NET Desktop Development** and **Windows App SDK** workloads) or VS Code

### Build Steps

1. **Clone the repository**:
   ```bash
   git clone https://github.com/YOUR_USERNAME/ContiExif.git
   cd ContiExif
   ```

2. **Restore dependencies**:
   ```bash
   dotnet restore
   ```

3. **Build and publish self-contained binary**:
   You can run the included batch script:
   ```cmd
   compile.bat
   ```
   Or manually run:
   ```bash
   dotnet publish -c Release -p:Platform=x64 -r win-x64 -o publish
   ```
   The compiled standalone application will be located in the `publish/` folder.

---

## 📁 Data Storage

All cached thumbnails, indexed metadata databases, and settings are stored locally in your user profile:
```
%USERPROFILE%\Documents\ContiTools\
```

---

## 📄 License

This project is licensed under the **GNU General Public License v3.0** (GNU GPLv3). See the [LICENSE](LICENSE) file for the full license text.
