# FileFlow Studio (BFR) v3.2 — Rebuild giao diện kiểu ứng dụng macOS

> Trạng thái: **Đã chốt hướng 26/11/2026** (xem §11). Còn mỗi Q5 đang dùng mặc định. Nhánh đề xuất: `feat/v3.2-macos-ui` từ `main`.
> Phạm vi: chỉ lớp trình bày trong `src/BFR.App` (Themes, Controls, MainWindow, Pages, Studios, cửa sổ phụ). Không đụng `BFR.Core`, `BFR.Store`, `BFR.Documents`, `BFR.Media`, CLI, Shell.
> Kế thừa: [BFR_MACOS_GLASS_BENTO_PLAN.md](BFR_MACOS_GLASS_BENTO_PLAN.md) (v2.1) — plan này thay thế phần bố cục/hiệu ứng của plan đó.

---

## 1. Chẩn đoán: vì sao giao diện hiện tại vẫn "cổ điển"

Ảnh chụp bản Debug ngày 25/11/2026 (Home, PDF Studio) cho thấy app đã có vài chi tiết "mượn" của macOS (chấm traffic light, sidebar, ô ⌘K) nhưng **cấu trúc vẫn là app Windows dạng danh sách thẻ**:

| # | Vấn đề | Ở đâu |
|---|---|---|
| 1 | Nền nội dung và sidebar cùng một màu đặc → Mica bị che, không có cảm giác "vibrancy" tách lớp như Finder | `MainWindow.xaml`, `Palette.*.xaml` |
| 2 | Toolbar là một hàng riêng (hamburger + ‹ › + tiêu đề) nằm *dưới* vùng traffic light, không phải **unified toolbar** | `MacToolbar.xaml` |
| 3 | Mọi thứ là "thẻ có viền" cùng trọng lượng (Home có 13 thẻ giống nhau) → không có điểm nhìn chính, giống trang Settings của Windows | `HomePage.cs`, `ToolsHomeView.cs` |
| 4 | Studio dùng 2 "bento card" nổi (Stage + Inspector) thay vì **pane** chạy hết chiều cao ngăn bằng đường kẻ 1px | `ToolWorkspaceLayout.cs`, `PdfStudioPage.cs` |
| 5 | Segmented control kiểu "viên thuốc đỏ" + tab chữ — không giống `NSSegmentedControl` (rãnh xám, núm trắng trượt) | `Bfr.Controls.xaml` |
| 6 | Màu nhấn đỏ cam rực dùng cho cả nút chính, tab, link, icon sidebar → quá nhiều điểm nóng | `Accent.*.xaml` |
| 7 | Gần như **không có chuyển động**: đổi trang, mở inspector, đổi tab, kéo thả… đều nhảy cứng | toàn app (chỉ Radial/Toast/Mascot có animation) |
| 8 | Tiêu đề dùng Plus Jakarta Sans (hơi "startup/Gen Z"), thang chữ không đều (13–22 lẫn lộn) | `Tokens.xaml` |
| 9 | Dialog/sheet/alert/popover chưa có ngôn ngữ chung; một số vẫn là `ContentDialog` của WPF-UI | `DialogChrome.cs`, các `*Content.cs` |

**Kết luận:** lần này không "phủ glass lên bố cục cũ" nữa, mà đổi **bộ khung cửa sổ + hệ control + hệ chuyển động**, sau đó cho từng trang chui vào khung mới.

---

## 2. Nguyên tắc thiết kế (đặc điểm app macOS cần đạt)

1. **Ba vùng rõ ràng**: Sidebar (source list, trong mờ) · Content (nền đặc, yên tĩnh) · Inspector (tuỳ chọn, ẩn/hiện được). Ngăn bằng đường kẻ 1px, không bằng thẻ nổi.
2. **Unified toolbar**: một hàng cao 52px duy nhất ở đỉnh, gồm tiêu đề trang + điều khiển của trang (segmented, search, nút hành động). Không có hàng tiêu đề riêng.
3. **Nội dung là chính, chrome lùi lại**: chữ xám phụ, icon đơn sắc, viền mờ. Màu nhấn chỉ dùng cho *1 nút chính / trang*, mục đang chọn và focus ring.
4. **Control đúng "chất" AppKit**: push button bezel nhỏ, popup button có chevron ⌃⌄, segmented control núm trắng, switch, search field bo tròn, bảng không kẻ ô + hàng xen màu, form dạng *inset grouped* (như System Settings).
5. **Chuyển động có vật lý**: spring nhẹ, ngắn (150–350ms), mọi thay đổi trạng thái đều có chuyển tiếp; tôn trọng *Reduce Motion*.
6. **Mật độ vừa phải**: chữ thân 13px, hàng bảng 24–28px, khoảng thở theo lưới 4/8px.
7. **Không phá Windows**: giữ snap layout, phím tắt Ctrl (hiển thị ⌘ chỉ là trang trí? → xem §11), DPI, High Contrast, Narrator.

### Giới hạn bản quyền (bắt buộc)
- **Không dùng SF Pro / SF Symbols / ảnh hệ thống của Apple** — giấy phép chỉ cho nền tảng Apple. Thay bằng: **Inter** (đã nhúng, gần SF nhất) và bộ icon **Lucide** (ISC, nét mảnh giống SF). Nhúng Lucide dạng `Geometry` path trong `Themes/Mac/Icons.xaml` (chỉ các icon dùng tới, ~120 cái) + control `MacIcon` (Kind, Size, stroke 1.5–2px); thêm giấy phép ISC của Lucide vào `THIRD_PARTY_NOTICES.md`. Thay dần glyph Segoe MDL2/Fluent đang rải trong code.
- Không sao chép icon app, hình nền macOS, âm thanh hệ thống.

---

## 3. Giới hạn kỹ thuật WPF & cách vượt

| Cần | WPF làm được không | Cách làm |
|---|---|---|
| Sidebar trong mờ (vibrancy) | Có ở mức cửa sổ | Bật backdrop **Mica Alt / Acrylic** cho cả cửa sổ; cột sidebar **trong suốt** để lộ backdrop, vùng content tô nền đặc `BfrWindowBg`. Win10/HC → nền đặc. |
| Blur phía sau một phần tử | Không | Không làm. Popover/menu/sheet dùng nền gần đặc (alpha 0.92–0.96) + bóng, không giả blur bằng `BlurEffect`. |
| Spring animation | Không có sẵn | Tự viết `SpringEase : EasingFunctionBase` (damped harmonic, tham số `Stiffness`, `Damping`). |
| Animate kích thước cột Grid | Không trực tiếp | Animate `Width` của panel con / dùng `GridLengthAnimation` tự viết; trong lúc animate bật `BitmapCache`. |
| Chuyển cảnh "card phóng thành trang" | Không có sẵn | Chụp `RenderTargetBitmap` của card → overlay `Image` animate từ rect card tới rect content, rồi fade trang thật vào. |
| Hiệu năng | Ổn nếu đúng cách | Chỉ animate `Opacity` + `RenderTransform`; không animate layout trên DataGrid/lưới thumbnail; không `DropShadowEffect` trên vùng cuộn. |

Giữ nguyên: **WPF + WPF-UI 4.3.0**, .NET 8, không `AllowsTransparency=True`, không đổi framework.

---

## 4. Design tokens mới (`Themes/Bfr/`)

Viết lại `Tokens.xaml`, `Palette.Light.xaml`, `Palette.Dark.xaml`; giữ tên khoá cũ làm alias trong 1 phiên bản để không vỡ code-behind, sau đó dọn.

**Chữ** (Inter; bỏ Plus Jakarta Sans khỏi UI chính, chỉ giữ cho Store asset nếu cần)

| Vai trò | Cỡ / độ đậm | Dùng cho |
|---|---|---|
| Large Title | 26 Bold | Tiêu đề trang Home/Tools |
| Title 2 | 17 SemiBold | Tiêu đề toolbar, tiêu đề sheet |
| Headline | 13 SemiBold | Tên nhóm form, tiêu đề hàng |
| Body | 13 Regular | Mặc định |
| Callout | 12 Regular | Mô tả dưới control |
| Footnote / Section | 11 SemiBold, màu Fg3 | Nhãn nhóm sidebar ("PDF", "TÀI LIỆU") |

**Màu (mỗi palette sáng/tối)** — mô phỏng semantic colors của AppKit:
`WindowBg`, `ContentBg`, `ControlBg`, `ControlBgPressed`, `SeparatorLine`, `GridLine`, `LabelPrimary/Secondary/Tertiary/Quaternary`, `SelectedContentBg` (accent), `UnemphasizedSelectedBg` (xám khi cửa sổ mất focus), `FocusRing` (accent alpha 0.5), `AlternatingRowBg`, `SidebarSelectedBg`.
**Accent** mặc định xanh macOS (`#007AFF` / `#0A84FF`), 8 lựa chọn kiểu macOS thay cho Sunset/Mint/Aurora. Migration: `SettingsStore.AccentPalette` cũ → map Sunset→Xanh (là mặc định cũ nên không phân biệt được người đã chọn; chốt 26/11/2026), Mint→Lục, Aurora→Tím, giá trị lạ → Xanh; có unit test.
Thêm trạng thái **cửa sổ không active**: mục chọn sidebar/bảng đổi sang xám, giống macOS (theo `Window.IsActive`).

**Hình khối**: control 6, nút lớn 8, card/group 10, sheet/popover 12, cửa sổ do Windows 11 bo. Viền 1px `SeparatorLine` (alpha thấp), không dùng viền accent.

**Chuyển động** (`Infrastructure/Motion/MotionTokens.cs`):

| Token | Giá trị | Dùng |
|---|---|---|
| `Micro` | 120ms ease-out | hover, press, toggle |
| `Standard` | spring (stiffness 300, damping 30) ≈ 250ms | đổi trang, segmented, popover |
| `Emphasized` | spring (stiffness 220, damping 24) ≈ 380ms, overshoot nhẹ | sheet, inspector, card → trang |
| `Exit` | 150ms ease-in | đóng popover/sheet/toast |
| `Stagger` | 25ms/item, tối đa 12 item | danh sách xuất hiện |

---

## 5. Khung cửa sổ mới (Shell)

```
┌───────────────┬──────────────────────────────────────────────────────────────┐
│ ● ● ●     [⊟] │  Tiêu đề trang   [ Seg | ment | ed ]        🔍 Tìm   [⋯] [◧] │ ← unified toolbar 52px
│               ├──────────────────────────────────────────────┬───────────────┤
│ Tìm kiếm      │                                              │  INSPECTOR    │
│               │                                              │  (form inset  │
│ ⌂ Bắt đầu     │              CONTENT                         │   grouped)    │
│ ★ Gần đây     │      (bảng / lưới thumbnail / canvas)        │               │
│ PDF           │                                              │               │
│   Sắp xếp…    │                                              │               │
│   Nén…        │                                              │               │
│ TÀI LIỆU      │                                              ├───────────────┤
│ TỆP           │                                              │ [  Áp dụng  ] │
│ THƯ VIỆN      ├──────────────────────────────────────────────┴───────────────┤
│ ─────────     │ 8 tệp · 6 sẽ đổi · 0 lỗi                     status bar 26px │
│ 👤 Pro (trial)│                                                              │
└───────────────┴──────────────────────────────────────────────────────────────┘
  sidebar 232px trong mờ          content nền đặc                  inspector 300px
```

Thành phần mới trong `Controls/Mac/`:

| Control | Vai trò |
|---|---|
| `MacWindowShell` | Grid 3 vùng, quản lý thu/mở sidebar & inspector (có animation), lưu độ rộng vào `SettingsStore` |
| `MacSourceList` | Sidebar: ô tìm, các section thu gọn được (chevron xoay), mục chọn nền accent bo 6, badge đếm, kéo-thả tệp lên mục để mở công cụ đó |
| `MacUnifiedToolbar` | Nhận nội dung từ trang qua `IToolbarProvider { Title, Subtitle, CenterItems, TrailingItems }`; trang đổi → nội dung toolbar crossfade |
| `MacInspectorPane` | Pane phải, header + body cuộn + footer cố định nút chính; toggle bằng nút ◧ / `Ctrl+Alt+I` |
| `MacStatusBar` | Dải 26px dưới content, chữ Footnote |
| `PageTransitionHost` | Thay cơ chế bật/tắt `Visibility` của `PageHost`; chạy transition theo hướng (tiến/lùi) |

Toolbar hiện tại (`MacToolbar`) và sidebar hiện tại (`MacSidebar`) được thay thế; `TopNavBar` đã không dùng → xoá.

---

## 6. Hệ control (restyle `Bfr.Controls.xaml` → tách `Mac.Controls.*.xaml`)

| Control | Kiểu macOS | Ghi chú |
|---|---|---|
| Push button | cao 24/28, bezel gradient rất nhẹ, bóng 1px; nút mặc định = nền accent | `Enter` kích hoạt nút mặc định như cũ |
| Toolbar button | icon 16, không viền, hover nền xám bo 6 | |
| Segmented control | rãnh xám, núm trắng (tối: xám sáng) **trượt** theo spring | thay `SegmentedRadioButtonStyle` |
| Popup button (ComboBox) | nhãn + chevron ⌃⌄, menu bật ra **căn mục đang chọn trùng vị trí** như macOS | |
| Pull-down / ⋯ menu | menu bo 8, nền gần đặc, hàng 22px, phím tắt căn phải màu Fg3 | `ContextMenu` + `MenuItem` |
| Switch | 26×15, núm trượt spring, màu accent khi bật | thay `ui:ToggleSwitch` style |
| Checkbox / Radio | 14px, bo 3 / tròn, dấu ✓ vẽ có animation | |
| Text field / Search field | bo 6 / bo pill, **focus ring accent 3px tỏa ra** (animate) | |
| Bảng (DataGrid) | header 22px chữ Footnote, không kẻ dọc, hàng xen màu, chọn hàng nền accent, mất focus → xám | giữ ảo hoá, không đổi binding |
| Form inset grouped | nhóm bo 10 nền `ControlBg`, hàng 36px, nhãn trái – control phải, kẻ ngang giữa hàng | dùng cho Inspector & Settings |
| Slider, Stepper, Progress (bar + spinner kiểu mac) | | |
| Tooltip | nền gần đặc, chữ 11, xuất hiện sau 600ms, fade 120ms | |
| Scrollbar | overlay 7px, chỉ hiện khi cuộn/hover, tự ẩn sau 1s | |

**Lưới công cụ (Home/Tools)**: icon **squircle màu theo nhóm** 48px (đã có `SquircleIcon`) + nhãn dưới, kiểu System Settings / Launchpad, thay cho thẻ dài có viền.

---

## 7. Hệ chuyển động & tương tác (phần user muốn nhất)

Hạ tầng: `Infrastructure/Motion/` gồm `SpringEase`, `MotionTokens`, `Motion` (API code-behind: `Motion.FadeIn`, `SlideIn`, `ScaleIn`, `Crossfade`, `AnimateWidth`) và các **attached property** dùng được cả trong XAML lẫn trang dựng bằng C#:
- `Motion.HoverLift="True"`, `Motion.PressScale="0.97"`, `Motion.AppearStagger="True"`, `Motion.Shake` (lỗi nhập).

Danh sách hiệu ứng cụ thể:

| # | Tương tác | Hiệu ứng |
|---|---|---|
| M1 | Đổi trang qua sidebar | Trang cũ fade-out 120ms; trang mới fade-in + trượt 12px theo hướng (xuống dưới sidebar = trượt lên, ‹ Back = trượt phải) |
| M2 | Mở công cụ từ Home/Tools | **Zoom transition**: card/icon phóng thành trang (snapshot overlay), ‹ Back thu ngược lại về đúng icon |
| M3 | Thu/mở sidebar, inspector | Width animate spring; nội dung content reflow mượt (BitmapCache trong lúc chạy) |
| M4 | Segmented control / tab mode của Studio | Núm trượt; nội dung inspector crossfade + chiều cao animate |
| M5 | Section sidebar thu gọn | Chevron xoay 90°, các mục con collapse theo chiều cao |
| M6 | Kéo tệp vào cửa sổ | Drop overlay hiện mờ dần, vùng drop "thở" (scale 1.00↔1.02), mục sidebar đang rê lên sáng lên; thả xong → icon tệp "bay" vào danh sách |
| M7 | Kéo-thả sắp xếp rule / trang PDF | Các phần tử khác **dạt ra nhường chỗ** (animate translate), phần tử đang kéo nổi lên + bóng |
| M8 | Danh sách xuất hiện lần đầu | Stagger fade+trượt 6px, tối đa 12 item (không áp cho bảng lớn) |
| M9 | Số liệu tóm tắt (6 sẽ đổi, 2 không đổi…) | Số đếm chạy (count-up 300ms) khi thay đổi |
| M10 | Sheet (xác nhận Áp dụng, nhập mật khẩu…) | Trượt xuống từ dưới toolbar + nền dim; đóng thì trượt ngược |
| M11 | Popover (rule trace, chọn token, bộ lọc) | Scale 0.96→1 từ điểm neo, mũi tên chỉ về nút |
| M12 | Áp dụng thành công | Progress → dấu ✓ vẽ nét (stroke animation) → banner kiểu thông báo macOS trượt vào góc trên phải, tự ẩn |
| M13 | Lỗi nhập (regex sai, mật khẩu sai) | Field rung ngang 3 nhịp (như ô mật khẩu macOS) |
| M14 | Hover/press mọi nút, hàng, icon | Micro 120ms: nền xám xuất hiện, press scale 0.97 |
| M15 | Cửa sổ mất/nhận focus | Mục chọn đổi accent ↔ xám (fade 150ms) |
| M16 | Command palette (⌘K) | Kiểu Spotlight: scale-in từ 0.98, kết quả lọc animate vị trí |

**Reduce Motion**: một công tắc trong Cài đặt + tự theo `SystemParameters.ClientAreaAnimation`. Khi bật: M1–M16 còn lại chỉ fade ≤120ms, tắt zoom/stagger/rung/count-up.

**Ngân sách hiệu năng**: mỗi transition ≤ 1 frame drop trên máy test; đo bằng `BFR.PerfGate` (thêm kịch bản: đổi trang 20 lần, mở/đóng inspector với 5.000 hàng trong bảng).

---

## 8. Bố cục từng màn

| Màn | Bố cục mới |
|---|---|
| **Bắt đầu (Home)** | Large title "Chào buổi tối" + vùng thả lớn ở giữa (nét đứt mờ → đặc khi rê tệp); dưới là **Gần đây** (5 thao tác/tệp gần nhất từ History, bấm để mở lại) và **Yêu thích** (icon squircle). Bỏ lưới 13 thẻ. |
| **Tất cả công cụ** | Lưới icon squircle theo nhóm (PDF / Tài liệu / Tệp & Ảnh), ô search lọc có animation sắp xếp lại; ★ ghim lên sidebar |
| **Đổi tên** | Toolbar: `+ Thêm` (pull-down: tệp/thư mục/Shelf) · popup Sắp xếp · segmented lọc (Tất cả/Sẽ đổi/Không đổi/Vấn đề) · search · nút **Áp dụng** (prominent). Content: bảng preview kiểu Finder list. Inspector: chuỗi quy tắc (preset popup + rule dạng hàng form, kéo-thả), Rule trace là popover trên hàng đang chọn. Status bar: số liệu. |
| **PDF Studio** | Toolbar: segmented chế độ (Sắp xếp · Nén · Đóng dấu · Bảo mật · Chuyển đổi · So sánh) căn giữa. Content: lưới thumbnail trên nền canvas xám (như Preview.app). Inspector: tuỳ chọn của chế độ + nút Lưu ở đáy. Bulk action bar = HUD viên thuốc nổi giữa đáy content. |
| **Docs & OCR, Media Clean** | Cùng khuôn với PDF Studio (dùng chung `MacWindowShell` + `MacInspectorPane`); OCR split view có divider kéo được |
| **Shelf / Watch / History / Presets** | Master–detail: danh sách trái (trong content), chi tiết phải (inspector). History có nhóm theo ngày (Hôm nay / Hôm qua / Tuần này) |
| **Cài đặt** | **Cửa sổ riêng kiểu Settings của macOS**: toolbar icon-tab (Chung · Giao diện · Phím tắt & Chuột · Menu tròn · Pro · Giới thiệu), chiều cao cửa sổ animate khi đổi tab; mở bằng `Ctrl+,` |
| **Dialog** | 3 loại: **Alert** (icon app, tiêu đề đậm, mô tả, nút phải-căn, nút mặc định accent), **Sheet** (gắn dưới toolbar), **Popover**. Thay dần `ContentDialogHost`. Upgrade Pro → sheet. |
| **Command palette** | Spotlight: 640px, giữa trên màn, ô nhập lớn 22px, kết quả nhóm, preview phải |
| **Toast** | Banner kiểu Notification Center: góc trên phải, 360px, icon app + tiêu đề + mô tả + nút hành động (Mở tệp / Thư mục / Hoàn tác / Làm tiếp ▸) hiện khi hover; nhiều banner xếp chồng, vuốt sang phải để đóng; tiến trình là thanh mảnh dưới banner |
| **Radial menu** | Giữ cơ chế 2 vòng + logic ngữ cảnh (không đổi `QuickActionCoordinator`), đổi ngôn ngữ hình: nền kính gần đặc + bóng lớn, ô hình quạt bo góc, icon Lucide, mục hover phóng nhẹ + nhãn hiện ở tâm; mở bằng scale+fade từ con trỏ (spring), vòng phụ trượt ra theo cung |
| **Floating Orb** | Hình tròn kính mờ 48px kiểu AssistiveTouch / Dock: icon app đơn sắc, mờ 60% khi rảnh, sáng lên khi hover; bám mép bằng spring; khi rê tệp lên thì nở to + vòng accent "thở" |
| **Mascot Tem** | Giữ, **vẽ lại** theo phong cách mới: hình khối tối giản, đơn sắc theo accent, nét kiểu icon (không gradient tím-hồng). Xuất hiện ở: màn chào lần đầu, empty state, banner thành công lớn. Không xuất hiện trong toolbar/sidebar |

**Quy tắc bắt buộc khi chuyển trang sang khung mới**: giữ mọi `x:Name`, binding, `AutomationProperties.Name` mà code-behind/UiTests đang dùng; chỉ đổi container. Chuỗi mới thêm đủ 4 file `Strings.{vi,en,fr,zh-Hans}.xaml`.

---

## 9. Lộ trình

Mỗi phase = 1 PR nhỏ vào nhánh `feat/v3.2-macos-ui`, build 0 warning + test xanh + ảnh chụp sáng/tối trước khi sang phase sau.

| Phase | Nội dung | Kết quả nghiệm thu | Ước lượng |
|---|---|---|---|
| **P0 Prototype** | Dựng `MacGalleryWindow` (thay `BfrGalleryWindow`) với token mới, 10 control chính, 1 khung shell giả + 4 hiệu ứng (M1, M3, M4, M10). Chụp ảnh gửi duyệt | User duyệt hướng nhìn trước khi đụng app thật | 2–3 ngày |
| **P1 Tokens + Controls** | `Tokens/Palette` mới (alias khoá cũ), `Mac.Controls.*.xaml`, scrollbar overlay, trạng thái inactive window | Gallery đủ control; test tương phản palette ≥ 4.5:1 | 3–4 ngày |
| **P2 Motion** | `SpringEase`, `Motion`, attached properties, Reduce Motion, kịch bản PerfGate | Unit test `SpringEase` (hội tụ, không NaN), PerfGate xanh | 2 ngày |
| **P3 Shell** | `MacWindowShell`, `MacSourceList`, `MacUnifiedToolbar` + `IToolbarProvider`, `MacInspectorPane`, `MacStatusBar`, `PageTransitionHost`, backdrop Mica Alt cho sidebar | Điều hướng mọi trang qua shell mới (nội dung trang chưa đổi) | 4–5 ngày |
| **P4 Đổi tên** | Trang quan trọng nhất; bảng kiểu Finder, inspector chuỗi quy tắc, sheet xác nhận, M7/M9/M12 | Smoke test `packaging/smoke-ui.ps1` + UiTests pass | 4 ngày |
| **P5 Studios** | Thay `ToolWorkspaceLayout` bằng shell/inspector; PDF Studio → Docs & OCR → Media Clean | Chạy đủ chế độ từng Studio, so ảnh trước/sau | 6–8 ngày (PdfStudioPage 4.800 dòng) |
| **P6 Home / Tools / Thư viện** | Bắt đầu + Gần đây, lưới squircle, master–detail cho Shelf/Watch/History/Presets, M2 zoom transition | | 4 ngày |
| **P7 Cửa sổ phụ** | Cửa sổ Cài đặt mới, Alert/Sheet/Popover, Command palette Spotlight, Regex tester | | 4 ngày |
| **P8 Ngoài cửa sổ chính** | Toast → banner, Radial menu, Floating Orb, vẽ lại Tem. Không đổi logic phát hiện ngữ cảnh / hook chuột / hotkey | Test `BFR.App.UiTests` liên quan Radial/Toast pass; mở Radial ≤ 120ms như cam kết v3.0 | 5 ngày |
| **P9 Polish + QA** | §10, ảnh Store v3.2, release notes | | 3 ngày |

Tổng ước lượng: **~6–7 tuần** làm một người (agent + review). Phát hành **một lần ở v3.2**, không có công tắc giao diện cũ/mới.

---

## 10. Kiểm thử & tiêu chí xong

- Build 0 warning; toàn bộ `tests/` pass, đặc biệt `BFR.App.UiTests`, `BFR.Presentation.Tests`, test khoá ngôn ngữ 4 file.
- Ma trận ảnh chụp: {Bắt đầu, Đổi tên rỗng/có tệp/có lỗi, PDF Studio 6 chế độ, Docs&OCR, Cài đặt} × {sáng, tối} × {vi, en} + 1 lượt fr/zh-Hans kiểm tra tràn chữ.
- Kích thước tối thiểu cửa sổ (1020×620): sidebar tự thu về icon, inspector tự ẩn thành popover, không cắt nội dung.
- High Contrast: không backdrop, viền đặc, focus ring rõ. Win10: nền đặc.
- Reduce Motion bật: không còn zoom/stagger/rung.
- Hiệu năng: 5.000 tệp trong bảng Đổi tên cuộn mượt; PDF 1.000 trang; PerfGate không lùi.
- Bàn phím & Narrator: thứ tự Tab theo vùng (sidebar → toolbar → content → inspector), `F6` nhảy giữa các vùng như macOS; mọi control có tên UIA.
- Theo memory dự án: backup/restore AppData (settings, history, journals) trước khi chạy test trên máy thật; không Apply thật dưới tier override Free.

---

## 11. Cần chốt (câu hỏi cho chủ dự án)

| # | Câu hỏi | Đề xuất mặc định |
|---|---|---|
| Q1 | Phong cách macOS nào? | ✅ **Chốt 25/11: Sequoia** — sidebar trong mờ dính mép, content nền đặc |
| Q2 | Điều hướng? | ✅ **Chốt: sidebar liệt kê thẳng mọi công cụ** theo nhóm; Home chỉ còn vùng thả + Gần đây + Yêu thích |
| Q3 | Nút cửa sổ? | ✅ **Chốt: traffic light bên trái** (giữ), thêm tooltip "Đóng / Thu nhỏ / Phóng to" + hover hiện ký hiệu × – + như macOS |
| Q4 | Màu nhấn mặc định? | ✅ **Chốt: xanh macOS** (`#007AFF` sáng / `#0A84FF` tối); Cài đặt vẫn cho chọn màu khác (thay bộ Sunset/Mint/Aurora bằng bảng màu kiểu macOS: Xanh, Tím, Hồng, Đỏ, Cam, Vàng, Lục, Graphite) |
| Q5 | Hiển thị phím tắt dạng **⌘ ⇧ ⌥** hay **Ctrl Shift Alt**? | ⏳ Mặc định: **Ctrl/Shift/Alt** (người dùng Windows bấm Ctrl), ô tìm ghi "Ctrl K" thay cho "⌘K" hiện tại |
| Q6 | Bộ icon? | ✅ **Chốt: Lucide** (xem §2) |
| Q7 | Cài đặt? | ✅ **Chốt: cửa sổ riêng** kiểu Settings macOS |
| Q8 | Phạm vi ngoài cửa sổ chính? | ✅ **Chốt: làm lại cả Toast, Radial menu, Floating Orb** (phase P8) |
| Q9 | Mascot Tem? | ✅ **Chốt: giữ và vẽ lại** theo phong cách mới (§8) |
| Q10 | Phát hành? | ✅ **Chốt: một lần ở v3.2**, làm trên nhánh `feat/v3.2-macos-ui` |
