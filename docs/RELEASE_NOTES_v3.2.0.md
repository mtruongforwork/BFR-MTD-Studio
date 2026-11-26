# FileFlow Studio (BFR) — Release Notes v3.2.0

Phiên bản gói MSIX: `3.2.0.0`

## Nổi bật: giao diện mới theo phong cách ứng dụng macOS

Toàn bộ giao diện được dựng lại. Tính năng, tệp cài đặt, preset, lịch sử và giấy phép Pro giữ nguyên.

### Khung cửa sổ
- **Thanh bên** liệt kê thẳng mọi công cụ theo nhóm (PDF, Tài liệu, Tệp & ảnh, Thư viện), có ô lọc, ghim công cụ yêu thích và thu gọn được.
- **Một thanh công cụ duy nhất** ở đỉnh cho tiêu đề và thao tác của trang; **bảng tuỳ chọn bên phải** đóng mở được (`Ctrl+Alt+I`); `F6` chuyển giữa các vùng.
- **8 màu nhấn** kiểu macOS (mặc định xanh), giao diện sáng, tối và tương phản cao.
- Chuyển động ngắn khi đổi trang, mở công cụ, đóng mở bảng. Công tắc **Giảm chuyển động** mới trong Cài đặt; app cũng làm theo cài đặt hiệu ứng của Windows.

### Từng màn
- **Trang chủ:** một vùng thả lớn, 5 thao tác gần nhất và công cụ yêu thích.
- **Tất cả công cụ:** lưới biểu tượng theo nhóm, lọc theo tên.
- **Đổi tên:** bảng xem trước rộng hơn, chuỗi quy tắc ở bảng bên phải, hỏi xác nhận ngay trên cửa sổ trước khi áp dụng và hỏi lại nếu danh sách đã đổi.
- **PDF Studio, Docs & OCR, Media:** tuỳ chọn và nút hành động chính chuyển sang bảng bên phải.
- **Khay tệp, Theo dõi thư mục, Lịch sử, Preset:** danh sách ở giữa, chi tiết bên phải; Lịch sử nhóm theo ngày.
- **Cài đặt** là cửa sổ riêng có 6 thẻ (`Ctrl+,`).
- **Bảng lệnh** (`Ctrl+K`) có nhóm kết quả và khung xem trước.

### Ngoài cửa sổ chính
- **Thông báo** dạng banner ở góc trên bên phải: xếp chồng, vuốt sang phải để đóng, có thanh tiến trình. Nút Mở tệp, Thư mục, Hoàn tác và Làm tiếp hiện khi rê chuột vào banner.
- **Menu tròn** vẽ lại với biểu tượng mới; cách nhận diện tệp, chuột và phím tắt không đổi.
- **Nút tròn nổi** gọn hơn, mờ đi khi để yên.
- Linh vật **Tem** được vẽ lại theo màu nhấn đang chọn.

## Sửa lỗi
- **Hoàn tác khi app bị tắt giữa chừng:** nếu app bị tắt đúng lúc đang áp dụng, tệp đã đổi tên giờ vẫn Hoàn tác được ở lần mở sau.
- Khôi phục sau sự cố xử lý đúng tệp chỉ đổi chữ hoa/thường và bước đổi tên đã thất bại.
- Hoàn tác thử lại khi tệp đang bị chương trình khác giữ trong chốc lát.
- **Mở nhiều tệp khi app đang tắt:** trên máy khởi động chậm, một số tệp có thể bị bỏ qua mà không báo. Giờ app chờ đủ lâu và báo lỗi nếu vẫn không gửi được.
- **Theo dõi thư mục:** thêm quy tắc đầu tiên không còn làm app đóng đột ngột; tệp đến trong lúc đang khởi động không còn bị bỏ sót; tắt quy tắc từ danh sách không còn bị bật lại.
- Công cụ yêu thích "OCR ngoại tuyến" và "Cách ly" mở đúng công cụ.
- Mở app kèm tệp vào thẳng trang Đổi tên.

## Trợ năng
- Đặt tên cho các ô nhập, danh sách chọn và thanh trượt ở PDF Studio, Docs & OCR và Media để trình đọc màn hình đọc được.

## Lưu ý khi nâng cấp
- Bảng màu nhấn cũ được chuyển sang màu mới: Sunset thành Xanh, Mint thành Lục, Aurora thành Tím.
- Từ v2.2.0, tham số `--file-list` bỏ qua tệp danh sách lớn hơn 4 MB và chỉ nhận tối đa 20.000 đường dẫn. Để nạp nhiều hơn, hãy mở thẳng thư mục.
