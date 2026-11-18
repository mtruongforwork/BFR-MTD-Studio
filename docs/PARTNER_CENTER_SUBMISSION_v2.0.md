# Hướng dẫn điền hồ sơ Partner Center cho v2.0.0 (cập nhật app và add-on Pro)

Văn bản soạn sẵn để dán nằm ở `docs/STORE_LISTING_v2.0.md`. Tài liệu này nói **điền ở đâu, điền gì, thứ tự nào**. Tên các mục theo giao diện Partner Center tiếng Anh; Microsoft đổi giao diện thường xuyên nên nếu tên nút hơi khác thì tìm mục gần nghĩa nhất.

## Bước 0. Chuẩn bị (làm trước khi mở Partner Center)

| Việc | Chi tiết |
| :--- | :--- |
| Gói | `artifacts/BatchFileRenamer-2.0.0.msix` (chưa ký, Store tự ký khi duyệt). SHA-256 bắt đầu `f970a481…` |
| Ảnh | `docs/store-assets/v2.0/01…07` (PNG 1600×900) |
| Logo Store | `packaging/msix/StoreListingAssets/AppTile_300x300.png` (1:1), `BoxArt_1080x1080.png` (1:1 lớn), `PosterArt_720x1080.png` (2:3) |
| Biểu tượng add-on | `packaging/msix/Assets/addon_pro_300x300.png` |
| Privacy policy | `https://mtruongforwork.github.io/Release-Batch-File-Renamer/privacy.html`. Mở bằng cửa sổ ẩn danh để chắc trang hiện được |
| Ghi chú phát hành | `docs/RELEASE_NOTES_v2.0.0.md` (phần en-US và vi-VN) |

**Mở đúng sản phẩm.** Ảnh bạn gửi trước đó là "Offline PDF Toolbox" (`9P0G6ZR05JHK`), không phải BFR. Vào **Apps and games** và mở **BFR - Batch File Renamer**.

**Đối chiếu Product Identity** (menu trái **Product management → Product Identity**):

| Partner Center | Phải khớp manifest |
| :--- | :--- |
| Package/Identity/Name | `MTDStudio.BFR-BatchFileRenamer` |
| Package/Identity/Publisher | `CN=F6CFEAEE-C755-4C7E-BDC5-FD89DA169656` |
| Package/Properties/PublisherDisplayName | `MTD Studio` |

Lệch một ký tự là gói bị từ chối ở bước Packages. Nếu lệch, gửi tôi giá trị đúng để sửa `packaging/msix/AppxManifest.xml` và đóng gói lại.

**Phiên bản.** Gói mới là `2.0.0.0`; phải lớn hơn phiên bản đang phát hành (bản trước là 1.x nên đạt).

---

## Phần A. Hồ sơ cập nhật ứng dụng

Ở trang **Application overview**, bấm **Update** để tạo submission mới (nếu đã có bản nháp thì mở bản nháp). Sáu mục hiện ra như trong ảnh bạn gửi. Mục nào đang ghi *Unchanged* (giữ nguyên từ bản trước) thì chỉ cần mở ra kiểm tra, không bắt buộc sửa.

### A1. Pricing and availability
| Ô | Giá trị |
| :--- | :--- |
| Markets | Giữ như bản trước (hoặc chọn tất cả thị trường bạn muốn bán) |
| Visibility | Public, hiện trong Store |
| Schedule | Chọn "Publish as soon as certification completes". Nếu muốn đồng bộ với đổi giá add-on thì chọn "Specific date" |
| Base price | **Free** |
| Free trial | Không bật (bản dùng thử 30 ngày nằm trong app, không phải của Store) |
| Sale pricing | Không |
| Organizational licensing | Giữ mặc định |

Giá 7,99 USD **không** nằm ở đây, mà ở add-on (Phần B).

### A2. Properties
| Ô | Giá trị |
| :--- | :--- |
| Category / Subcategory | Utilities & tools (nếu có phụ mục thì chọn "File management" hoặc gần nhất) |
| Privacy policy URL | URL GitHub Pages ở Bước 0 |
| Website | Trang GitHub của dự án (tuỳ chọn) |
| Support contact info | Email hỗ trợ hoặc trang GitHub Issues. **Phải là liên lạc công khai thật** |
| System requirements | Tối thiểu: Windows 10 phiên bản 2004 (build 19041) trở lên, x64. Khuyến nghị 4 GB RAM |
| Product declarations | Không tick "đã kiểm thử trợ năng" nếu chưa kiểm bằng Narrator thật. Tôi mới quét cây UIA, chưa chạy Narrator |

### A3. Age ratings
Làm bảng câu hỏi (IARC) cho ứng dụng tiện ích: mọi câu về bạo lực, nội dung người lớn, cờ bạc, ngôn ngữ thô tục trả lời **Không**. Câu về mua hàng trong ứng dụng, trả lời **Có** (add-on Pro). Câu về chia sẻ vị trí hoặc tương tác người dùng: **Không** (app chỉ xử lý tệp cục bộ). Kết quả mong đợi: 3+ / Everyone. Mục này trong ảnh đang là *Complete*, nếu không đổi hành vi thì giữ nguyên.

### A4. Packages
1. Kéo `BatchFileRenamer-2.0.0.msix` vào ô upload. Chờ trạng thái **Validated**.
2. Nếu có lỗi validation, thường là Identity không khớp (xem Bước 0). Đừng cố sửa trên Partner Center.
3. Gói chỉ khai báo ngôn ngữ **vi-VN** và **en-US**. Đúng ra app có 4 ngôn ngữ (cả Pháp và Trung giản thể), nhưng ngôn ngữ chưa khai báo trong manifest sẽ không hiện trên Store. Để nguyên 2 ngôn ngữ cho lần này; muốn thêm fr/zh thì phải thêm `Resource Language` vào manifest và đóng gói lại, đừng làm bây giờ.
4. Tuỳ chọn: chọn phát hành dần (gradual rollout), ví dụ 25% rồi tăng, để giảm rủi ro cho người dùng cũ.

### A5. Store listings
Thêm hai ngôn ngữ **vi-VN** và **en-US** (menu *Add/remove languages*). Với mỗi ngôn ngữ:

| Ô | Dán từ |
| :--- | :--- |
| Description | `STORE_LISTING_v2.0.md`, mục tương ứng (vi hoặc en) |
| What's new in this version | `RELEASE_NOTES_v2.0.0.md`, phần en-US hoặc vi-VN |
| Product features (tối đa 20 dòng) | Dùng 8 gạch đầu dòng: Xem trước thời gian thực; Hoàn tác mọi thay đổi; Sắp xếp vào thư mục; Tìm tệp trùng; ZIP và checksum; Phân tích dung lượng dạng treemap; Menu chuột phải Explorer; 100% xử lý cục bộ |
| Screenshots | Tải 7 ảnh `docs/store-assets/v2.0/`. Ảnh `01` đặt đầu tiên. Mỗi ảnh thêm chú thích ngắn |
| Store logos | 1:1 `AppTile_300x300.png`, 2:3 `PosterArt_720x1080.png`, 1:1 lớn `BoxArt_1080x1080.png` nếu có ô |
| Short description | Mô tả ngắn trong `STORE_LISTING_v2.0.md` (tối đa 200 ký tự) |
| Search terms | Từ khoá trong `STORE_LISTING_v2.0.md` (tối đa 7 cụm) |
| Copyright and trademark info | `© 2026 MTD Studio` |
| Developed by | `MTD Studio` |

Ảnh chụp đều là tiếng Anh; dùng chung cho cả vi-VN và en-US (chưa có bản tiếng Việt).

### A6. Submission options
| Ô | Giá trị |
| :--- | :--- |
| Publishing hold options | Chọn phát hành ngay sau khi duyệt, hoặc "Hold" nếu bạn muốn chọn ngày |
| Notes for certification | Dán **nguyên khối** ở mục 3 của `docs/STORE_LISTING.md`, rồi thêm hai đoạn 9 và 10 ở cuối `STORE_LISTING_v2.0.md`. Đây là chỗ giải thích `runFullTrust`, nếu bỏ trống bài duyệt dễ bị trả về |
| Sign-in required to test | Không |
| Restricted capabilities | Ghi `runFullTrust` cần cho truy cập hệ thống tệp, theo đúng lý do trong Notes. Dán lại đoạn "Justification for runFullTrust" nếu có ô riêng |

### A7. Nộp
Chỉ bấm **Submit for certification** **sau khi** add-on ở Phần B đã tạo và nộp cùng đợt, vì app gọi mua Pro qua `BFR_PRO_LIFETIME`. Thời gian duyệt thường vài ngày làm việc (tôi không có số chính xác).

---

## Phần B. Hồ sơ add-on Pro (`BFR_PRO_LIFETIME`)

### B0. Kiểm tra add-on đã có chưa
Menu trái trong sản phẩm BFR → **Add-ons**. Nếu đã có `BFR_PRO_LIFETIME` (có thể bạn tạo từ bản 1.x) thì **mở nó và cập nhật**, không tạo mới. Product ID không đổi được và không dùng lại được.

### B1. Tạo mới (nếu chưa có): Product identity
Bấm **New add-on → Durable add-on**.

| Ô | Giá trị |
| :--- | :--- |
| Product ID | `BFR_PRO_LIFETIME` — **đúng từng ký tự, phân biệt hoa thường**. Code app dùng đúng chuỗi này; gõ sai thì nút Mua không hoạt động |
| Product type | Durable |
| Product lifetime | Forever |

### B2. Properties
Chọn loại nội dung gần nhất với "mở khoá tính năng của ứng dụng" (nếu có lựa chọn kiểu Other / Online service thì chọn cái đó). Không phải thuê bao (subscription = Không).

### B3. Pricing and availability
| Ô | Giá trị |
| :--- | :--- |
| Markets / Visibility | Giống app |
| Base price | **7,99 USD** (chọn mức giá gần nhất trong bảng giá của Partner Center) |
| Free trial | Không |
| Sale | Không |

- Giá mới chỉ áp cho người mua **sau** khi gói được duyệt. Người đã mua trước đó (giá 3,99 USD) vẫn sở hữu Pro, không phải trả thêm.
- Partner Center tự quy đổi giá theo khu vực.
- Đổi giá cùng thời điểm với phát hành app 2.0 để thông điệp thống nhất.

### B4. Age ratings
Làm lại bảng câu hỏi như app (đều trả lời Không).

### B5. Store listings (vi-VN và en-US)
| Ô | vi-VN | en-US |
| :--- | :--- | :--- |
| Title | Batch File Renamer Pro | Batch File Renamer Pro |
| Description | Mô tả add-on (vi) trong `STORE_LISTING_v2.0.md` | Mô tả add-on (en) trong `STORE_LISTING_v2.0.md` |
| Icon | `packaging/msix/Assets/addon_pro_300x300.png` | như vi |

Có thể liệt kê trong Description các tính năng Pro từ `docs/STORE_LISTING.md` mục 6, ví dụ: chuỗi quy tắc không giới hạn, quét đệ quy không giới hạn, ngày EXIF, hoàn tác nhiều bước và lưu lịch sử 30 ngày, preset không giới hạn và nhập/xuất `.bfrpreset`, xuất báo cáo CSV/JSON, công cụ dòng lệnh `bfr`, Shelf không giới hạn, đổi tên theo danh sách không giới hạn, Watch Folder, Workflow, tìm ảnh gần giống.

### B6. Nộp
Nộp add-on **cùng lúc** với app (Phần A7). Add-on không bán được cho tới khi app chứa nó được duyệt và phát hành.

---

## Phần C. Sau khi duyệt

1. Cài bản từ Store trên một máy sạch (Windows 10 build 19041+ hoặc Windows 11).
2. Mở Cài đặt BFR, kiểm tra Trang chủ, đổi tên một tệp, hoàn tác.
3. Mở hộp thoại **Nâng cấp lên Pro** và kiểm tra giá hiển thị đúng 7,99 USD.
4. Thử mua bằng một tài khoản Microsoft **khác** tài khoản nhà phát triển, rồi hoàn tiền nếu cần. Luồng mua Pro thật chưa ai chạy thử (kể cả tôi), nên bước này quan trọng.
5. Kiểm tra menu chuột phải Explorer có đủ 11 mục.

## Việc tôi **chưa** kiểm chứng
- Gói MSIX chưa được cài thử trên máy sạch.
- Luồng mua Pro qua Microsoft Store chưa chạy.
- Narrator thật và chế độ tương phản cao thật của Windows chưa thử (mới mô phỏng).
- Tôi không nhìn thấy tài khoản Partner Center của bạn. Mọi tên mục và giá trị ở trên dựa trên tài liệu trong repo và kiến thức chung về Partner Center, không phải trạng thái thật của hồ sơ.
