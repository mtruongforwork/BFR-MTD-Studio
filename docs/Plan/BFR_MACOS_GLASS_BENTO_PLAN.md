# BFR v2.1 — Rebuild UI/UX: macOS + Bento + Glass

> Plan cho agent code (agy). Nhánh mới: `feat/v2.1-glass-bento` từ `main`. v2.0.0 đã ship, không đụng logic Core/Store.
> Chỉ đổi lớp trình bày: `src/BFR.App/Themes/*`, `MainWindow.xaml`, các Page/View, `Infrastructure/ThemeManager.cs`.

## 1. Mục tiêu và cảm giác

| Trục | Mô tả |
|---|---|
| macOS | Sidebar trong mờ kiểu Finder, traffic-light-style title bar (nút Windows vẫn ở phải, không giả nút Mac), bo góc lớn, chữ nhẹ, nhiều khoảng thở, animation spring mềm. |
| Bento | Mỗi trang = lưới ô (tile) kích thước khác nhau, mỗi ô một việc. Không còn cột dài cuộn một mạch. |
| Glass | Ô nửa trong suốt, viền sáng 1px phía trên, bóng mềm, nền phía sau có màu loang để thấy được hiệu ứng trong. |

Giữ nguyên: 4 ngôn ngữ, 3 accent (Sunset/Mint/Aurora), sáng/tối, High Contrast, UIA/Narrator names, min-size window.

## 2. Giới hạn kỹ thuật WPF (đọc trước khi code)

- WPF **không có** backdrop blur cho phần tử bên trong cửa sổ. `BlurEffect` chỉ làm mờ chính phần tử đó, không làm mờ thứ nằm sau.
- Blur thật chỉ có ở mức cửa sổ qua DWM (Mica/Acrylic/Tabbed, Win11 22H2+). Hiện đã dùng Mica trong [ThemeManager.cs](../../src/BFR.App/Infrastructure/ThemeManager.cs).
- Vì vậy "glass" trong cửa sổ được **giả** bằng 4 lớp (xem mục 4). Không dùng `AllowsTransparency=True` (mất DWM, chậm, hỏng snap/Mica).
- `BlurEffect` render bằng phần mềm. Chỉ dùng cho các blob nền **tĩnh**, bọc `CacheMode=BitmapCache`. Không đặt effect lên DataGrid hay list cuộn.
- Win10 / High Contrast: tắt blob + glass, rơi về nền đặc (đã có cơ chế `BfrTheme.IsHighContrast`).

## 3. Design tokens mới (thêm vào `Themes/Bfr/`, không xoá token cũ)

Tạo `Glass.xaml` + `Bento.xaml`, merge trong `App.xaml` sau `Tokens.xaml`.

**Hình khối**
- `BfrRadiusTile` = 24 (ô lớn), `BfrRadiusTileSm` = 18, `BfrRadiusControl` giữ 12, `BfrRadiusSidebar` = 0 (bo ở cửa sổ do Win11 lo).
- Khoảng cách bento: `BfrBentoGap` = 12, lề trang `BfrPagePad` = 20. Đơn vị lưới 4 px như cũ.

**Glass (mỗi palette sáng/tối định nghĩa riêng)**
- `BfrGlassFill`: tối `#14FFFFFF`, sáng `#99FFFFFF`.
- `BfrGlassFillStrong` (sidebar, toolbar): tối `#1FFFFFFF`, sáng `#B3FFFFFF`.
- `BfrGlassRim`: LinearGradientBrush viền 1px, trên `#40FFFFFF` → dưới `#08FFFFFF` (sáng: trên `#FFFFFFFF` → dưới `#33FFFFFF`).
- `BfrGlassShadow`: DropShadowEffect blur 28, depth 8, opacity 0.28 (tối) / 0.12 (sáng), `ShadowDepth` hướng xuống.
- `BfrBackdropBlob1..3`: 3 màu loang lấy từ accent hiện hành (alpha 0.35–0.5).

**Chữ (kiểu SF)**: giữ Inter body, đổi tiêu đề từ Plus Jakarta Sans sang Inter SemiBold để bớt "Gen Z", gần SF hơn. Cỡ: Large Title 28, Title 20, Headline 15 SemiBold, Body 13, Caption 11. Letter-spacing không chỉnh được trong WPF, bỏ qua.

**Chuyển động**: thêm `BfrEaseSpring` (`BackEase` Amplitude 0.25 / hoặc `ElasticEase` rất nhẹ). Hover tile: nổi 2 px + bóng đậm hơn, 160 ms. Chuyển trang: fade + trượt 8 px, 220 ms (đã có, giữ).

## 4. Công thức một "Glass Tile" (control chính)

Tạo `Controls/GlassTile.cs` (custom `ContentControl`) + style trong `Glass.xaml`. Template, từ dưới lên:

1. `Border` nền `BfrGlassFill`, `CornerRadius` theo tile, `Effect` = `BfrGlassShadow`.
2. Lớp sáng: `Border` cùng bo góc, `Background` = LinearGradient trắng alpha 0.10 (trên) → 0 (dưới), `IsHitTestVisible=False`.
3. Viền: `Border` `BorderBrush=BfrGlassRim`, `BorderThickness=1`.
4. `ContentPresenter` với `Padding=BfrPadCard`.

Thuộc tính: `Header` (string), `Icon` (Segoe Fluent glyph), `Span` (ColSpan/RowSpan dùng khi đặt vào lưới), `IsInteractive` (bật hover nổi + focus ring).

Phụ: `GlassPanel` (không padding, cho sidebar/toolbar), `GlassButton` (pill, hover sáng thêm), `SegmentedGlass` (thay SegmentedRadioButtonStyle: thanh trượt nền accent, animate vị trí).

**Nền cửa sổ** (`MainWindow`): `Grid` gốc trong suốt (để Mica lộ) + `Canvas` chứa 3 `Ellipse` 420–600 px, `Fill` = blob accent, `Effect=BlurEffect Radius=120`, `CacheMode=BitmapCache`, đặt lệch góc. Có thêm class `Infrastructure/BackdropBlobs.cs` để đổi vị trí chậm theo trang đang mở (tuỳ chọn, mặc định tĩnh).

## 5. Layout cửa sổ kiểu macOS

- Bỏ `NavRail` 72 px. Thay bằng **Sidebar 220 px** (`GlassPanel` Strong): nhóm "Rename / Tools / Library" có nhãn chữ nhỏ viết hoa mờ, mục chọn = pill nền accent alpha 0.18 + icon accent (giống Finder). Thu về 72 px (icon-only) khi cửa sổ < 1000 px hoặc user bấm nút thu gọn.
- Title bar: dùng `ui:TitleBar` hiện có, nền trong suốt, tiêu đề căn giữa nhỏ, ô tìm kiếm/command palette dạng pill ở giữa (`Ctrl+K` đã có `CommandPaletteWindow`, chỉ nhúng nút mở).
- Vùng nội dung: `Grid` bento trong `ScrollViewer` mỏng (thanh cuộn overlay 6 px, chỉ hiện khi hover).
- Thanh trạng thái / Apply: tách khỏi trang, thành **floating action bar** dưới cùng, `GlassPanel` bo pill, cách đáy 16 px, chứa trạng thái + nút Apply (primary accent).

## 6. Bố cục bento từng trang

Lưới 12 cột (`UniformGrid` không đủ, dùng `Grid` 12 cột × hàng auto; viết `Controls/BentoPanel.cs` kế thừa `Panel` nhận `ColSpan`/`RowSpan`, tự bố trí, co về 1 cột khi hẹp < 900 px — dễ test hơn nhiều `Grid` lồng nhau).

**Rename (trang chính)** — sửa [MainWindow.xaml](../../src/BFR.App/MainWindow.xaml) phần `RenamePage`:

| Tile | Span | Nội dung |
|---|---|---|
| Drop zone | 12×2 khi rỗng, thu còn 12×1 khi có file | vùng thả file/thư mục, mascot Tem, nút Add |
| Rules | 5×4 | preset combo, danh sách rule (card nhỏ trong tile), nút thêm rule |
| Preview | 7×4 | DataGrid preview + filter segmented + search |
| Summary | 3×1 | số file đổi / giữ / lỗi (số lớn, màu ok/warn/bad) |
| Quick options | 4×1 | sort, case, extension toggles |
| Undo / History peek | 5×1 | 3 lần đổi gần nhất, nút Undo |

**Home**: tile chào + trạng thái tier Free/Pro (span 6×2), 4 tile công cụ nhanh (3×2), tile gần đây (6×2), tile mẹo (6×1).
**Tools**: mỗi công cụ một tile 4×2 có icon lớn + mô tả 1 dòng; bấm mở công cụ dạng trang con có nút Back kiểu macOS (‹ Tools).
**History / Presets / Shelf / Watch / Settings**: giữ nội dung, bọc từng nhóm vào `GlassTile`; Settings thành lưới tile theo chủ đề (Appearance, Language, Behavior, Pro/License, About) thay danh sách dài.
**Dialog phụ** (Regex tester, Command palette, Upgrade, Shelf window): dùng chung `DialogChrome` + nền blob nhỏ + `GlassTile`; Command palette làm kiểu Spotlight (giữa màn, 640 px, bo 24, bóng lớn).

## 7. Lộ trình (mỗi phase một commit, build + chạy kiểm tra trước khi qua phase sau)

1. **Foundation**: `Glass.xaml`, `Bento.xaml`, palette sáng/tối + 3 accent, `GlassTile`, `GlassPanel`, `BentoPanel`. Thêm tile mẫu vào [BfrGalleryWindow.xaml](../../src/BFR.App/BfrGalleryWindow.xaml) để xem riêng trước.
2. **Shell**: sidebar 220/72, title bar, blob backdrop, floating action bar, ThemeManager bật/tắt blob + fallback Win10/HC.
3. **Rename page** thành bento (trang quan trọng nhất, làm kỹ nhất; giữ nguyên mọi `x:Name` và binding để code-behind/test không vỡ).
4. **Home, Tools, History, Presets, Shelf, Watch, Settings**.
5. **Dialogs + Command palette** kiểu Spotlight.
6. **Polish**: spring/hover/animation chuyển trang, thanh cuộn overlay, empty states, đọc lại 4 file `Strings.*.xaml` nếu thêm nhãn nhóm sidebar (thêm key ở cả 4 ngôn ngữ).
7. **QA** (mục 8).

## 8. Kiểm thử và tiêu chí xong

- Build 0 warning; chạy toàn bộ test hiện có (`tests/`), không test nào đổi hành vi.
- Chụp ảnh bằng UIA/screenshot cho: Rename (rỗng / có file / có lỗi), Home, Tools, Settings × sáng/tối × 3 accent × `vi` và `en`. Xem bằng mắt, không chỉ build.
- Cửa sổ min-size: bento co về 1 cột, không cắt nội dung, không scroll ngang.
- High Contrast (`BFR_HC=1`): không blob, viền đặc, tương phản đạt. Win10 giả lập: nền đặc.
- Tương phản chữ trên glass: kiểm tra giá trị màu trong unit test tương phản đã có (palette tách `Color` khỏi `Brush` chính là để làm việc này). Chữ `Fg3` trên glass sáng phải ≥ 4.5:1.
- Hiệu năng: mở app + kéo thả 5.000 file, preview cuộn mượt (không tụt khung do blob/shadow). Nếu tụt: bỏ shadow trên tile chứa DataGrid, giảm blur radius blob, đảm bảo BitmapCache.
- Tab/Narrator: mọi tile tương tác có `AutomationProperties.Name`; thứ tự Tab theo thứ tự đọc.
- **Theo memory dự án**: trước khi chạy test trên AppData thật, backup/restore settings, history, journals; không chạy Apply thật dưới tier override Free.

## 9. Việc không làm trong v2.1

- Không giả nút traffic-light đỏ/vàng/xanh (gây nhầm nút đóng/thu phóng trên Windows).
- Không dùng `AllowsTransparency`, không đổi framework (vẫn WPF + WPF-UI 4.3.0).
- Không đụng Core/Store/CLI/Shell, không đổi giá/license.

## 10. Rủi ro

| Rủi ro | Giảm thiểu |
|---|---|
| Glass giả nhìn "bẩn" trên nền sáng | Tăng alpha fill ở theme sáng, thêm viền tối nhẹ phía dưới; xem ảnh chụp từng phase |
| Blur chậm trên máy yếu | Blob tĩnh + BitmapCache, công tắc "Giảm hiệu ứng" trong Settings (tắt blob + shadow) |
| `BentoPanel` tự viết lỗi layout | Viết unit test đo/sắp xếp (cột, span, co 1 cột) trước khi dùng |
| Vỡ binding khi di chuyển XAML | Giữ nguyên `x:Name`, chỉ đổi container; build + chạy sau mỗi trang |
