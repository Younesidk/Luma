# Luma: Avalonia to WinUI 3 Migration Plan

## 1. Objective & Hard Boundaries
Rewrite the desktop presentation layer (`src/Luma.App`) from **Avalonia UI** to **WinUI 3 (Windows App SDK)** on **.NET 11** (`net11.0-windows10.0.19041.0`) while preserving 100% feature parity, visual identity, offline-first library behavior, live desktop video wallpapers, and Velopack auto-updates.

### Do NOT Touch (Read-Only Layers)
- **`src/Luma.Core/`**: 0% changes. All models, enums, interfaces, validation, layout math, and `PngEncoder` remain untouched.
- **`src/Luma.Infrastructure/`**: 0% changes. EF Core SQLite DB/migrations, Wallhaven/Openverse providers, `ImageFetcher`, `DownloadService`, `RotationService`, and the entire native Win32/D3D11/MediaEngine desktop video wallpaper host (`Platform/Windows/Video/*`) remain untouched.
- **`src/Luma.Tests/`**: Keep all existing unit & integration tests passing. Only update tests in `AppLogicTests.cs` if they directly reference Avalonia types.

---

## 2. Locked Architectural Decisions (No Guessing)

Follow these decisions strictly across all phases. Do not introduce alternative libraries or patterns without explicit approval.

1. **Deployment & Entry Point (Unpackaged + Velopack)**
   - Configure `Luma.App.csproj` for unpackaged self-contained WinUI 3:
     - `<TargetFramework>net11.0-windows10.0.19041.0</TargetFramework>`
     - `<TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>`
     - `<UseWinUI>true</UseWinUI>`
     - `<WindowsPackageType>None</WindowsPackageType>`
     - `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>`
     - `<DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>`
     - `<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>`
   - Keep a custom `[STAThread] Program.Main` so `VelopackApp.Build().Run()`, `SingleInstance.TryAcquire()`, `--background` startup handling, and `LaunchAtLogin.RefreshIfEnabled()` execute **before** `WinRT.ComWrappersSupport.InitializeComWrappers()` and `Application.Start(...)`.

2. **UI Thread Dispatching (`IUiDispatcher`)**
   - Introduce `IUiDispatcher` (`void Post(Action action); Task InvokeAsync(Action action);`) in `src/Luma.App/Services/Interfaces.cs`, implemented via `Microsoft.UI.Dispatching.DispatcherQueue`.
   - Replace every `Avalonia.Threading.Dispatcher.UIThread` call in ViewModels (`CollectionsViewModel`, `DiscoverViewModel`, `DownloadsViewModel`, `HistoryViewModel`, `LiveWallpapersViewModel`, `UpdateViewModel`, `ShellViewModel`) and Services (`DialogService`, `LibraryEventsBridge`, `ToastService`, `TrayIconController`).

3. **System Tray Icon (`TrayIconController`)**
   - Use **`H.NotifyIcon.WinUI`** (`TaskbarIcon`) backed by `Assets/luma.ico`.
   - Preserve exact menu behavior: `Open Luma`, dynamic `Pause/Resume live wallpaper` (visible only while `Playing`/`Paused`), separator, `Quit Luma`, plus the 120-char truncated tooltip wired to `IVideoWallpaperService.StatusChanged`.
   - Closing `MainWindow` hides it to the tray whenever `TrayIconController.IsVisible` is true; real exit only occurs on tray `Quit Luma` or Velopack `RestartRequested`.

4. **Video Preview (`VideoPreviewHost`)**
   - **Do NOT** attempt an `HwndHost` / child-HWND hack inside WinUI 3.
   - Replace `VideoPreviewHost` with WinUI 3's **`MediaPlayerElement`** (`Windows.Media.Playback.MediaPlayer`) configured for muted, looping local playback (`IsLoopingEnabled = true`, `IsMuted = true`, `AutoPlay = true`, `Stretch = Uniform`).
   - Wire `MediaPlayer.MediaFailed` to update the bound `Error` dependency property via `IUiDispatcher` so `LiveWallpapersView` shows its existing error `EmptyState`.
   - Dispose the `MediaPlayer` cleanly on `Unloaded` or when `Source` changes to avoid file locks.

5. **Image Decoding & Transcoding (WIC — Zero Skia Dependency)**
   - **`ImageLoader.cs`**: Keep the exact LRU + ref-counted `ImageLease` architecture, `CachedCount`, and `CachedBytes` (required by `MemoryDiagnostics`).
     - Decode bytes off the UI thread using **WIC (`Windows.Graphics.Imaging.BitmapDecoder`)** scaled via `BitmapTransform` (`ScaledWidth`, preserving aspect ratio) into a BGRA8 premultiplied `SoftwareBitmap`.
     - Create/assign `SoftwareBitmapSource` on the UI thread via `IUiDispatcher`. Dispose underlying `SoftwareBitmap` / `SoftwareBitmapSource` only when `RefCount == 0` and evicted/uncached.
     - Update `PreviewViewModel.Image` from `Avalonia.Media.Imaging.Bitmap?` to `Microsoft.UI.Xaml.Media.ImageSource?`.
   - **`ImageTranscoder.cs`**: Implement `IImageTranscoder.ConvertToPngAsync` using WIC `BitmapDecoder` + `BitmapEncoder.PngEncoderId` so WebP/GIF wallpapers convert to PNG cleanly with zero third-party image libraries.

6. **Lucide Stroke Icons (`LucideIcon`)**
   - **Do NOT** use `PathIcon` (it fills paths instead of stroking them and breaks Lucide outlines).
   - Build `Controls/LucideIcon.cs` (a `Control` or `UserControl`) rendering a 24×24 `<Viewbox><Path ... /></Viewbox>` with:
     - `Data` (`string` path geometry data — reuse the exact SVG path strings from `Styles/Icons.axaml`)
     - `Size` (`double`, default `18`)
     - `StrokeThickness` (`double`, default `1.75`, scaled to the 24-unit grid with `StrokeStartLineCap="Round"`, `StrokeEndLineCap="Round"`, `StrokeLineJoin="Round"`)
     - `IsFilled` (`bool` — sets `Fill` to `Foreground` when true, e.g., active favorite heart)
     - Inherits `Foreground` automatically from parent buttons/states.

7. **Styling, Themes & Dynamic Accent (`ThemeService`)**
   - Translate `Styles/Tokens.axaml` into `Styles/Tokens.xaml` with `Dark` and `Light` `ThemeDictionaries` preserving Luma's exact color tokens (`LumaBackgroundBrush`, `LumaSidebarBrush`, `LumaSurfaceBrush`, `LumaElevatedBrush`, `LumaAccentBrush`, `LumaOnAccentBrush`, etc.).
   - Replace Avalonia CSS class selectors (`Classes="primary"`, `Classes="secondary"`, `Classes="glass"`, `Classes="card"`, `Classes="pill"`) with explicit keyed WinUI `Style` resources in `Styles/Controls.xaml`.
   - `ThemeService` sets `RequestedTheme` on the root `FrameworkElement` (`ElementTheme.Default` / `Dark` / `Light`) and mutates `Color` on existing `SolidColorBrush` instances (or swaps theme dictionary brushes + refreshes root theme) for all 6 `AccentPreset` values (`violet`, `blue`, `teal`, `green`, `amber`, `rose`).

8. **Controls & Deletions**
   - **Delete `SmoothScroll.cs`**: WinUI 3 `ScrollViewer` / `ScrollView` provides native Composition smooth scrolling.
   - **Data Binding**: Prefer compiled `{x:Bind}` wherever practical (`x:DataType` is already present on every view). Use static helper methods in `Views/BindHelpers.cs` for simple boolean/null/string visibility & text transforms instead of boilerplate `IValueConverter`s where cleaner.
   - **File Pickers (`FileDialogService`)**: Use `Windows.Storage.Pickers.FileOpenPicker` initialized with `WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle)` for image and video multi-file import.
   - **Graphics Setting**: WinUI 3 uses DirectX/Composition exclusively. If `SettingsView` exposes Avalonia's software/GPU toggle, remove that UI row (keep the property in `AppSettings` so existing user settings JSON deserializes cleanly).

---

## 3. File-by-File Migration Map (`src/Luma.App`)

### Keep Untouched (or <5 line tweaks)
- `Services/Updates/UpdateModels.cs`, `UpdateService.cs`, `VelopackUpdateClient.cs`
- `Services/FeedbackService.cs`, `LaunchAtLogin.cs`, `LumaServices.cs`, `MemoryDiagnostics.cs`, `Messages.cs`, `NavigationService.cs`, `ShellStartup.cs`, `SingleInstance.cs`, `WallpaperActions.cs`
- `ViewModels/DiscoverFilters.cs`, `FavoritesViewModel.cs`, `FeedbackViewModel.cs`, `PageViewModel.cs`, `WallpaperItemViewModel.cs`
- `appsettings.json`, `Assets/luma.ico`, `Assets/luma.png`

### Update Dispatcher / Image Type Only
- `Services/Interfaces.cs` (add `IUiDispatcher`)
- `Services/DialogService.cs`, `LibraryEventsBridge.cs`, `ToastService.cs`
- `ViewModels/CollectionsViewModel.cs`, `DiscoverViewModel.cs`, `DownloadsViewModel.cs`, `HistoryViewModel.cs`, `LiveWallpapersViewModel.cs`, `PreviewViewModel.cs`, `SettingsViewModel.cs`, `ShellViewModel.cs`, `UpdateViewModel.cs`

### Rewrite for WinUI 3
- **Project & Host:** `Luma.App.csproj`, `Program.cs`, `LumaHost.cs`, `App.xaml(.cs)`, `app.manifest`, `Directory.Packages.props`
- **Services:** `Services/UiDispatcher.cs` (new), `ThemeService.cs`, `ImageLoader.cs`, `ImageTranscoder.cs`, `FileDialogService.cs`, `TrayIconController.cs`
- **Styles:** `Styles/Tokens.xaml`, `Styles/Icons.xaml`, `Styles/Controls.xaml`, `Styles/Components.xaml`
- **Controls:** `Controls/LucideIcon.cs`, `Controls/Components.cs` (`EmptyState`, `SettingRow`), `Controls/ThumbnailImage.cs`, `Controls/VideoPreviewHost.cs`, `Controls/WallpaperCard.xaml(.cs)`, `Controls/WallpaperGrid.xaml(.cs)`
- **Views:** `MainWindow.xaml(.cs)`, `NavButton.xaml(.cs)`, `DiscoverView.xaml(.cs)`, `FeaturedHero.xaml(.cs)`, `FilterPanel.xaml(.cs)`, `FavoritesView.xaml(.cs)`, `DownloadsView.xaml(.cs)`, `CollectionsView.xaml(.cs)`, `HistoryView.xaml(.cs)`, `LiveWallpapersView.xaml(.cs)`, `SettingsView.xaml(.cs)`, `FeedbackView.xaml(.cs)`, `PreviewOverlay.xaml(.cs)`, `ConfirmDialogView.xaml(.cs)`, `CollectionEditDialogView.xaml(.cs)`, `CollectionPickerDialogView.xaml(.cs)`, `Converters.cs`
- **Packaging/CI:** `scripts/publish.ps1`, `scripts/build-installer.ps1`, `.github/workflows/ci.yml`, `.github/workflows/release.yml`

---

## 4. Execution Phases & Verification Gates

Every phase MUST end with a clean `dotnet build Luma.sln` and `dotnet test Luma.sln` before moving to the next phase.

### Phase 1 — Decouple ViewModels & Stand Up Empty WinUI 3 Shell
1. Add `IUiDispatcher` and remove all `Avalonia.*` imports from `ViewModels/` and platform-neutral `Services/` (except `PreviewViewModel.Image`, which switches to `ImageSource`).
2. Update `Directory.Packages.props` and `Luma.App.csproj`: remove Avalonia packages; add `Microsoft.WindowsAppSDK` and `H.NotifyIcon.WinUI`.
3. Implement `UiDispatcher`, `Program.cs` (Velopack + SingleInstance + WinUI bootstrap), `LumaHost.cs`, `App.xaml(.cs)`, and a minimal `MainWindow.xaml` displaying a placeholder title.
4. Rewrite `ThemeService`, `ImageLoader` (WIC + `SoftwareBitmapSource` + LRU leases), `ImageTranscoder` (WIC PNG), `FileDialogService`, and `TrayIconController`.
- **Gate:** `dotnet build Luma.sln` succeeds, `dotnet test Luma.sln` passes, app launches cleanly, tray icon appears and quits cleanly.

### Phase 2 — Design System & Core Custom Controls
1. Create `Styles/Tokens.xaml`, `Styles/Icons.xaml` (string path data), `Styles/Controls.xaml`, and `Styles/Components.xaml`.
2. Build `LucideIcon`, `EmptyState`, `SettingRow`.
3. Build `ThumbnailImage` (loads on `Loaded`/source change, cancels & releases `ImageLease` on `Unloaded`, fade-in animation, `FallbackSource`, `UniformToFill`, `IsFailed`/`IsLoading`).
4. Build `WallpaperCard` (hover scale via Composition/VisualState, hover-reveal top/bottom gradient scrims + metadata + action buttons, persistent favorite heart, saved badge, progress bar, `MenuFlyout` context menu).
5. Build `WallpaperGrid` (responsive virtualized grid with `ItemsRepeater` + `UniformGridLayout` or row-virtualized `ListView`, optional `Header` for `FeaturedHero`, skeleton loading state, footer with `Load more` / `Loading more…` / end note, infinite-scroll trigger).
6. Build `VideoPreviewHost` using `MediaPlayerElement`.
- **Gate:** `dotnet build Luma.sln` succeeds with zero XAML compiler warnings/errors.

### Phase 3 — MainWindow Shell, Overlays & Discover Experience
1. Implement `MainWindow.xaml(.cs)` and `NavButton.xaml(.cs)`:
   - Responsive sidebar (auto-collapses to icons on narrow widths), custom title bar integration, window sizing bounded to monitor work area.
   - Keyboard shortcuts: `Ctrl+K` (focus Discover search), `Ctrl+R` (refresh), `Esc` (close preview/dialog), `Left`/`Right` (preview prev/next).
   - Floating `UpdateCard`, corner toast stack (`ToastService`), modal dialog overlay (`DialogService`), `PreviewOverlay`.
2. Implement `DiscoverView`, `FeaturedHero`, `FilterPanel`, and `PreviewOverlay`.
3. Implement the 3 dialogs: `ConfirmDialogView`, `CollectionEditDialogView`, `CollectionPickerDialogView`.
- **Gate:** App runs end-to-end; browsing Wallhaven/Openverse, filtering, opening full-res preview, favoriting, downloading, and applying a static wallpaper all work.

### Phase 4 — Remaining Pages (Library, Live Wallpapers, Settings, Feedback)
1. Implement `FavoritesView`, `DownloadsView`, `CollectionsView` (overview mosaic + detail view), and `HistoryView`.
2. Implement `LiveWallpapersView` (now-playing bar, import progress, video cards, dark preview overlay with `VideoPreviewHost`, display & fit mode selectors).
3. Implement `SettingsView` and `FeedbackView`.
- **Gate:** Every navigation tab renders and functions; importing, previewing, applying, pausing/resuming, and stopping a live desktop video wallpaper works seamlessly alongside WinUI 3; theme & accent switching updates live.

### Phase 5 — Visual Polish, Memory Check & Velopack Packaging
1. Verify memory stays bounded while scrolling Discover (run with `LUMA_MEMLOG=1` and confirm off-screen thumbnails release leases and `decodedThumbs` stays within budget).
2. Polish animations, focus rings, DPI scaling (100% / 125% / 150%), and close-to-tray + `--background` launch behavior.
3. Update `scripts/publish.ps1`, `scripts/build-installer.ps1`, `.github/workflows/ci.yml`, and `.github/workflows/release.yml` for `win-x64` unpackaged WinUI 3 output and verify Velopack `Setup.exe` + full/delta packaging.
- **Gate:** `dotnet build -c Release`, `dotnet test -c Release`, and `./scripts/build-installer.ps1` all succeed cleanly.