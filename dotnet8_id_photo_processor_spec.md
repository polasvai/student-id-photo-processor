# Project Specification: Automated Batch Student ID Photo Processor (.NET 8)

## 1. Project Overview & Objective
Build a complete, high-performance web application using **.NET 8** that processes batch-uploaded mobile photos into standardized Student ID card photos. 

### Core Functional Requirements:
1. **Batch Upload:** Users can upload multiple photos simultaneously (taken via mobile cameras in various orientations and aspect ratios).
2. **EXIF Auto-Orientation:** Automatically fix image orientation based on mobile EXIF metadata before processing.
3. **Smart Face & Shoulder Cropping:** Detect the human face and calculate a centered bounding box that includes the head, shoulders, and a 12-15% top margin (headroom) matching the target aspect ratio.
4. **AI Background Removal:** Remove the original background cleanly (including fine hair details) using a local ONNX segmentation model (`RMBG-1.4` or `ModNet` / `U2Net`).
5. **Solid Background Replacement:** Replace the transparent background with a customizable solid off-white color (Default: Hex `#F4F4F4` / RGB `244, 244, 244`).
6. **Custom Resizing:** Resize the cropped and composited image to user-defined dimensions (Default: `300x300` pixels) using high-quality Lanczos3 resampling.
7. **Batch Export:** Allow users to preview processed photos, manually re-crop if needed, and download all processed images packaged in a single `.zip` archive (preserving original filenames).

---

## 2. Technology Stack & NuGet Packages
- **Framework:** .NET 8 (ASP.NET Core Web App / Blazor Interactive Server or Web API + Razor/Blazor UI)
- **Image Processing:** `SixLabors.ImageSharp` (v3.1.x+) — *Strictly avoid `System.Drawing.Common` for cross-platform safety and high performance.*
- **AI Model Inference:** `Microsoft.ML.OnnxRuntime` (CPU execution provider by default, configurable for CUDA/DirectML).
- **Face Detection:** `OpenCvSharp4` + `OpenCvSharp4.runtime.win` / `linux` (using YuNet ONNX or Haar Cascade) OR `UltraFace` via `Microsoft.ML.OnnxRuntime`.
- **Concurrency & Queueing:** `System.Threading.Channels` with a `BackgroundService` worker to prevent memory spikes during large batch uploads.

---

## 3. Solution Architecture & Folder Structure
Create a clean, single-solution architecture:

```text
StudentIdPhotoApp/
├── Program.cs
├── StudentIdPhotoApp.csproj
├── Models/
│   ├── ProcessingOptions.cs       // TargetWidth, TargetHeight, BgColorHex, HeadroomRatio
│   └── PhotoJobStatus.cs          // JobId, FileName, Status, OutputBytes, ErrorMessage
├── Services/
│   ├── IOnnxModelBootstrapper.cs  // Auto-downloads ONNX models on startup if missing
│   ├── IFaceCropService.cs        // Detects face and returns optimal ID photo crop rectangle
│   ├── IBackgroundRemovalService.cs // Runs ONNX segmentation and returns Alpha Matte
│   ├── IPhotoPipelineService.cs   // Orchestrates EXIF -> Face Crop -> BG Remove -> Composite -> Resize
│   └── BatchProcessingWorker.cs   // BackgroundService reading from Channel<PhotoJob>
├── wwwroot/
│   └── models/                    // Local directory storing rmbg-1.4.onnx and yunet.onnx
└── Components / Controllers/
    ├── PhotoBatchController.cs    // API endpoints for batch upload, status polling, and ZIP download
    └── Pages/Index.razor          // Drag-and-drop UI, configuration inputs, progress bar, and preview grid
```

---

## 4. Detailed Step-by-Step Implementation Guide

### Step 1: Model Bootstrapper (`OnnxModelBootstrapper.cs`)
To ensure the project runs out-of-the-box without manual model hunting, implement an `IHostedService` or startup routine that checks `wwwroot/models/` and automatically downloads the required ONNX models if they do not exist:
1. **Background Removal Model:** `model.onnx` (BRIA `RMBG-1.4` quantized or `u2netp.onnx` from HuggingFace/GitHub release).
2. **Face Detection Model:** `face_detection_yunet_2023mar.onnx` (from OpenCV Zoo on HuggingFace/GitHub).

### Step 2: Image Loading & EXIF Normalization
Using `SixLabors.ImageSharp`:
- Load the uploaded stream into an `Image<Rgba32>`.
- Immediately call `image.Mutate(x => x.AutoOrient());` to fix mobile portrait rotation issues.

### Step 3: Smart ID Card Face Framing (`FaceCropService.cs`)
Do not perform a naive center crop. Implement intelligent framing based on the detected face bounding box $(x, y, w, h)$:
1. Run the image through the Face Detection ONNX model to get the primary face rectangle $(x, y, w, h)$.
2. Calculate the face center:
   $$\text{Center}_X = x + \frac{w}{2}, \quad \text{Center}_Y = y + \frac{h}{2}$$
3. Calculate target crop box dimensions based on the user's desired aspect ratio $R = \frac{\text{TargetWidth}}{\text{TargetHeight}}$:
   - Set target crop height: $H_{\text{crop}} = h \times 2.35$ (includes head, neck, and upper shoulders).
   - Set target crop width: $W_{\text{crop}} = H_{\text{crop}} \times R$.
4. Position the top of the crop box to guarantee headroom above the hair:
   - $Y_1 = y - (h \times 0.45)$
   - $X_1 = \text{Center}_X - \frac{W_{\text{crop}}}{2}$
5. Clamp $(X_1, Y_1, W_{\text{crop}}, H_{\text{crop}})$ safely within the source image boundaries (or pad with off-white if the subject is too close to the edge of the camera frame).
6. Return the cropped `Image<Rgba32>`. *(Fallback: If no face is detected, perform a top-weighted center crop).*

### Step 4: ONNX Background Segmentation (`BackgroundRemovalService.cs`)
1. **Singleton Session:** Register `InferenceSession` as a Singleton in DI so the ONNX model is loaded into memory only once.
2. **Pre-processing:**
   - Resize a copy of the cropped image to the model's input size ($1024 \times 1024$ for RMBG-1.4, or $320 \times 320$ for U2Net).
   - Normalize pixel values to float tensors:
     $$\text{Normalized} = \frac{\frac{\text{Pixel}}{255.0} - 0.5}{1.0}$$
   - Construct a `DenseTensor<float>` with shape `[1, 3, H, W]`.
3. **Inference:**
   - Run `_session.Run(...)` and extract the output tensor `[1, 1, H, W]`.
4. **Post-processing (Alpha Mask Generation):**
   - Normalize the output tensor using Min-Max scaling to $[0, 255]$.
   - Convert the tensor back into a grayscale `Image<L8>` mask and resize it back to the exact dimensions of the cropped image using Bicubic interpolation.

### Step 5: Off-White Compositing & Final Resizing (`PhotoPipelineService.cs`)
1. Parse the user's desired background color (Default: `#F4F4F4` / `new Rgba32(244, 244, 244, 255)`).
2. Create a new output `Image<Rgba32>` of size `(TargetWidth, TargetHeight)`.
3. Resize both the cropped foreground image and the generated alpha mask to `(TargetWidth, TargetHeight)` using `KnownResamplers.Lanczos3`.
4. **Alpha Blending (Pixel-by-Pixel via `ProcessPixelRows` for zero-allocation speed):**
   For every pixel $(x, y)$, let $\alpha = \frac{\text{Mask}(x, y)}{255.0}$:
   $$R_{\text{out}} = \text{ round}(R_{\text{fg}} \cdot \alpha + R_{\text{bg}} \cdot (1 - \alpha))$$
   $$G_{\text{out}} = \text{ round}(G_{\text{fg}} \cdot \alpha + G_{\text{bg}} \cdot (1 - \alpha))$$
   $$B_{\text{out}} = \text{ round}(B_{\text{fg}} \cdot \alpha + B_{\text{bg}} \cdot (1 - \alpha))$$
5. Encode the final image as high-quality JPEG (`JpegEncoder { Quality = 95 }`) or PNG.

### Step 6: Batch Queue & ZIP Generation
- Use `Channel.CreateBounded<BatchPhotoJob>(500)` so uploading 100+ high-res mobile photos (4-8 MB each) does not cause Out-Of-Memory (OOM) exceptions.
- Process images sequentially or with limited parallelism (`MaxDegreeOfParallelism = 2` or `4`).
- Provide an endpoint `/api/batch/download/{batchId}` that streams a `ZipArchive` directly to the client response using `System.IO.Compression.ZipArchive`.

---

## 5. Frontend UI Requirements
Build a clean, modern single-page dashboard (using Bootstrap 5 or Tailwind CSS):
1. **Configuration Panel:**
   - Width Input (px) — Default: `300`
   - Height Input (px) — Default: `300`
   - Background Color Picker — Default: `#F4F4F4` (Off-White), with quick presets for `#FFFFFF` (Pure White) and `#EBF4FA` (Passport Light Blue).
2. **Drag & Drop Zone:**
   - Supports selecting 1 to 200+ `.jpg`, `.jpeg`, `.png`, `.webp` files at once.
3. **Real-Time Progress & Preview Grid:**
   - Displays a progress bar (`Processed 14 / 50 photos...`).
   - Shows a grid of processed 300x300 thumbnails side-by-side with their filenames.
4. **Action Buttons:**
   - **"Download All as .ZIP"** button once batch processing finishes.
   - Individual download button on each card.

---

## 6. Execution Instructions for the AI Coder (Google Antigravity)
Please implement the complete, production-ready **.NET 8 solution** following the exact architecture above. 
- Do **not** leave placeholder comments like `// TODO: implement ONNX logic`. Write the full tensor manipulation and `ImageSharp` `ProcessPixelRows` blending code.
- Ensure all `IDisposable` objects (`Image`, `InferenceSession`, `MemoryStream`) are properly managed.
- Include a `README.md` explaining how to run `dotnet restore` and `dotnet run`.