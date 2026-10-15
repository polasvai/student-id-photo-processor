<div align="center">

# 🎓 Student ID Photo Processor (.NET 8)

**An enterprise-grade, privacy-first, automated batch photo processing engine and web application that converts candid mobile snapshots into standardized, high-resolution Student ID and Passport photos.**

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![C# 12](https://img.shields.io/badge/C%23-12-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/en-us/dotnet/csharp/)
[![Blazor Server](https://img.shields.io/badge/Blazor-Interactive_Server-512BD4?logo=blazor&logoColor=white)](https://dotnet.microsoft.com/apps/aspnet/web-apps/blazor)
[![ONNX Runtime](https://img.shields.io/badge/ONNX_Runtime-v1.17+-005CED?logo=onnx&logoColor=white)](https://onnxruntime.ai/)
[![SixLabors ImageSharp](https://img.shields.io/badge/SixLabors-ImageSharp_3.1-orange)](https://sixlabors.com/products/imagesharp/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

[Features](#-key-features) • [Architecture](#-architecture) • [Getting Started](#-getting-started) • [Deployment](#-deployment--hosting) • [API Reference](#-api-endpoints) • [Tech Specs](#-technical-specifications)

</div>

---

## 📌 Project Overview

Processing hundreds or thousands of student ID photos every semester is typically tedious and inconsistent. Photos taken on mobile cameras have random backgrounds, varying lighting, inconsistent head sizes, and different orientations.

**Student ID Photo Processor** automates the entire pipeline locally on your machine or server:
1. **Auto-corrects** mobile EXIF orientation.
2. **Detects human faces** with sub-millimeter precision using **YuNet ONNX**.
3. **Calculates standard ID framing** (headroom, neck, and shoulder ratios).
4. **Removes complex backgrounds** locally using state-of-the-art **BRIA RMBG-1.4** segmentation.
5. **Composites** onto clean institutional background colors (solid white, off-white, light blue, or custom hex).
6. **Preserves 100% of original filenames** (Student ID numbers).
7. **Embeds target DPI resolution** (300 DPI, 600 DPI, 150 DPI) for print-ready output.
8. **Exports** single photos or full batches in a streaming `.zip` archive.

> 🔒 **100% Private & Offline:** No photos or student data are ever sent to third-party cloud APIs. All AI inference is executed locally using ONNX Runtime.

---

## ✨ Key Features

- **⚡ High-Throughput Batch Processing:** Upload 1 to 200+ photos at once (supports JPEG, PNG, WEBP).
- **🆔 Exact Filename Preservation:** Crucial for schools and universities—preserves filenames like `2024001.jpg` without appending suffixes.
- **🖨️ Print-Ready DPI Embedding:** Injects resolution metadata (`300 DPI` for print lab standards, `600 DPI` for ultra-fine cards, or `72 DPI` for web).
- **🤖 YuNet ONNX Face Detection:** Ultra-fast, lightweight face detection that accurately finds faces even with glasses, masks, head angles, or varying skin tones.
- **✂️ RMBG-1.4 Neural Background Removal:** Accurately masks fine strands of hair and subtle clothing edges.
- **🎨 Custom Background Controls:**
  - Presets: Off-White (`#F4F4F4`), Pure White (`#FFFFFF`), Light Blue (`#EBF4FA`).
  - Interactive HTML5 Color Picker & Hex input for custom institutional colors.
- **🎛️ Manual Framing Sliders & Presets:**
  - Adjust **Headroom Ratio** (0.10 to 0.90) and **Torso Zoom Factor** (1.50 to 3.50).
  - Presets for *Standard ID*, *Tight / Close-up*, and *Relaxed / Wide Shoulders*.
  - **"Save as My Default"** button stores user preferences in browser `localStorage`.
- **🚀 Zero-Allocation Compositing:** Custom alpha compositing powered by `ImageSharp`'s `ProcessPixelRows` for direct memory access without GC thrashing.
- **🛡️ Bounded Queue Architecture:** Built with `System.Threading.Channels` (`BoundedCapacity = 500`) to guarantee stability without Out-Of-Memory (OOM) crashes under heavy load.
- **💻 Desktop & Server Ready:** Run as a 1-click portable desktop app on Windows or deploy as a background service on Linux/Windows Server.

---

## 🏗️ Architecture

```text
StudentIdPhotoApp.sln
├── StudentIdPhotoApp/                      # Main ASP.NET Core & Blazor Server Web Application
│   ├── Program.cs                         # Application bootstrapper, DI, and limits (500MB upload)
│   ├── Models/
│   │   ├── ProcessingOptions.cs           # Width, Height, TargetDpi, Headroom, TorsoZoom, BgColorHex
│   │   ├── PhotoJobStatus.cs              # Job state tracking (Queued, Processing, Completed, Failed)
│   │   └── BatchPhotoJob.cs               # Channel job model
│   ├── Services/
│   │   ├── IOnnxModelBootstrapper.cs      # Contract for model integrity verification
│   │   ├── OnnxModelBootstrapper.cs       # BackgroundService auto-downloading YuNet & RMBG-1.4
│   │   ├── IFaceCropService.cs            # Face detection & framing contract
│   │   ├── FaceCropService.cs             # YuNet ONNX face detection & smart cropping math
│   │   ├── IBackgroundRemovalService.cs   # Neural segmentation contract
│   │   ├── BackgroundRemovalService.cs    # Singleton ONNX RMBG-1.4 inference & alpha matte
│   │   ├── IPhotoPipelineService.cs       # Pipeline orchestrator contract
│   │   ├── PhotoPipelineService.cs        # EXIF -> Crop -> BG Removal -> Compositing -> Lanczos3
│   │   ├── IBatchProcessingService.cs     # Queue & batch status manager contract
│   │   └── BatchProcessingWorker.cs       # Channel-driven BackgroundService worker
│   ├── Controllers/
│   │   └── PhotoBatchController.cs        # REST API (Upload, Status, Preview, Single/Batch Download)
│   ├── Components/
│   │   └── Pages/Index.razor              # Modern Blazor drag-and-drop dashboard
│   └── wwwroot/
│       └── models/                        # Pre-bundled local ONNX models (.onnx)
├── StudentIdPhotoApp.DesktopLauncher/     # Portable 1-click Windows Desktop Launcher
│   └── Program.cs                         # Silent server bootstrap + chromeless Edge app window
└── StudentIdPhotoApp.Tests/               # Automated unit & integration tests
    ├── FaceCropServiceTests.cs            # Framing boundary verification
    ├── ProcessingOptionsTests.cs          # Validation and defaults
    ├── PhotoPipelineServiceTests.cs       # End-to-end image processing tests
    └── BatchPhotoControllerTests.cs       # REST API endpoint tests
```

---

## 🚀 Getting Started

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or higher.

### 1. Clone the Repository
```bash
git clone https://github.com/your-username/student-id-photo-processor.git
cd student-id-photo-processor
```

### 2. Restore Dependencies & Build
```bash
dotnet restore
dotnet build
```

### 3. Run the Web Application
```bash
cd StudentIdPhotoApp
dotnet run
```

> **Note on First Run:** If the ONNX model files are not found locally, the built-in `OnnxModelBootstrapper` will automatically download them from secure Hugging Face / GitHub releases:
> - `face_detection_yunet_2023mar.onnx` (~232 KB)
> - `rmbg-1.4.onnx` (~176 MB)

Once initialized, navigate to:
```text
http://localhost:5209
```

---

## 📦 Deployment & Hosting

### 1. Portable Desktop Application (Windows)
Run the application completely standalone as a portable desktop app with 1 click:
```bash
dotnet publish StudentIdPhotoApp.DesktopLauncher/StudentIdPhotoApp.DesktopLauncher.csproj -c Release -r win-x64 --self-contained true -o dist/Desktop-App-Portable-Windows
```
- Launch using `Launch-StudentIdPhotoApp.exe` or `Run-Desktop.bat`.
- Opens directly into a clean, chromeless application window with no browser URL bar.

### 2. Linux Server (Ubuntu / Debian / RHEL / Docker)
Deploy as a high-performance Linux daemon:
```bash
dotnet publish StudentIdPhotoApp/StudentIdPhotoApp.csproj -c Release -r linux-x64 --self-contained true -o /var/www/studentidphoto
```

#### Systemd Service (`/etc/systemd/system/studentidphoto.service`):
```ini
[Unit]
Description=Student ID Photo Processor
After=network.target

[Service]
WorkingDirectory=/var/www/studentidphoto
ExecStart=/var/www/studentidphoto/StudentIdPhotoApp
Restart=always
RestartSec=10
SyslogIdentifier=studentidphoto-app
User=www-data
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5209

[Install]
WantedBy=multi-user.target
```
Enable and start the service:
```bash
sudo systemctl daemon-reload
sudo systemctl enable --now studentidphoto.service
```

#### Nginx Reverse Proxy:
```nginx
server {
    listen 80;
    server_name photos.yourinstitution.edu;

    # Support large batch photo uploads (up to 500 MB)
    client_max_body_size 500M;

    location / {
        proxy_pass         http://127.0.0.1:5209;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade $http_upgrade;
        proxy_set_header   Connection "upgrade";
        proxy_set_header   Host $host;
        proxy_cache_bypass $http_upgrade;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
    }
}
```

### 3. Windows Server (IIS)
1. Install the **ASP.NET Core .NET 8 Hosting Bundle**.
2. Deploy the published files into an IIS website folder.
3. The included `web.config` will automatically configure the in-process handler with request limits up to 500 MB.

---

## 📡 API Endpoints

| Method | Endpoint | Description |
|---|---|---|
| `POST` | `/api/batch/upload` | Multipart upload for 1–200+ photos. Returns `batchId`. |
| `GET` | `/api/batch/status/{batchId}` | Polls batch progress (counts of queued, processing, completed, failed). |
| `GET` | `/api/batch/preview/{jobId}` | Returns processed image stream for browser thumbnail rendering. |
| `GET` | `/api/batch/download-single/{jobId}` | Downloads individual student photo with exact original filename. |
| `GET` | `/api/batch/download/{batchId}` | Streams a ZIP archive of all processed photos (original filenames preserved). |
| `GET` | `/api/batch/models/status` | Health check endpoint returning ONNX model readiness status. |

---

## 🔬 Technical Specifications

### Smart Framing Mathematics
Given a detected face bounding box $(x, y, w, h)$, user headroom ratio $R_{\text{head}}$, torso factor $F_{\text{torso}}$, and target aspect ratio $A = \frac{W_{\text{target}}}{H_{\text{target}}}$:

$$\text{Center}_X = x + \frac{w}{2}$$

$$H_{\text{crop}} = h \times F_{\text{torso}} \quad (\text{default: } 2.35)$$

$$W_{\text{crop}} = H_{\text{crop}} \times A$$

$$Y_{\text{origin}} = y - (h \times R_{\text{head}}) \quad (\text{default: } 0.45)$$

$$X_{\text{origin}} = \text{Center}_X - \frac{W_{\text{crop}}}{2}$$

*Edge Padding:* If the crop bounds extend beyond the raw image dimensions, the boundaries are safely padded with the chosen background color without stretching the face.

### Zero-Allocation Compositing
Using `SixLabors.ImageSharp` `ProcessPixelRows` to directly manipulate scanlines:
$$\alpha = \frac{\text{Mask}(x, y)}{255.0}$$
$$\text{Color}_{\text{out}} = \text{round}\Big(\text{Foreground} \cdot \alpha + \text{Background} \cdot (1 - \alpha)\Big)$$

---

## 🧪 Running Automated Tests

Run the full xUnit test suite covering the face crop math, options validation, and photo pipeline:
```bash
dotnet test StudentIdPhotoApp.Tests
```

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
The bundled AI models are subject to their respective licenses:
- **YuNet:** OpenCV License / Apache 2.0
- **BRIA RMBG-1.4:** BRIA AI Open Model License
