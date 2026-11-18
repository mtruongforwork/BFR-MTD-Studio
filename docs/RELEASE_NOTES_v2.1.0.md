# BFR - Batch File Renamer — Release Notes v2.1.0

Tài liệu ghi chú phát hành cho **Microsoft Partner Center**, bản **v2.1.0** (Package Version: `2.1.0.0`).

> **Giá và Pro:** không đổi so với v2.0. Người đã mua Pro giữ nguyên quyền, không phải trả thêm.
> Bản này không thêm tính năng trả phí mới; tập trung vào giao diện mới và độ ổn định.

---

## What's New in this Version (English - en-US)

### Release highlights for v2.1.0:
- **A new look.** A Mac-style sidebar with labels (collapsible to icons), frosted-glass cards, and tile layouts on the home page, the Tools page, Settings and the rename screen (live counts of files that will change, stay the same or have problems). Works in light and dark, with all three accent palettes, and in high contrast.
- **Reduce visual effects.** A new switch in Settings turns off blur, glow and shadows. It is switched on automatically on PCs without graphics acceleration (some virtual machines, Remote Desktop), where the effects made the app noticeably slower.
- **Recover from an interrupted rename.** If the app is closed or crashes while applying a rename, the next start tells you and the run appears in History as "Interrupted", so you can undo it and get your original file names back. Nothing is changed until you choose Undo.
- **Much faster with huge lists.** Opening 20,000 files used to freeze the window for 10+ seconds, and 100,000 files for minutes. 100,000 files now load in a few seconds.
- **The app keeps running after an unexpected screen error.** You see a short message, your files are untouched, and details go to the error log. Repeated errors still stop the app safely.
- **More reliable under stress.** Hardened against locked or read-only files, long and unusual file names, files that change while you work, many windows opened at once from Explorer, and slow or missing network when checking for updates.
- **Accessibility.** Tiles on the home and Tools pages can now be activated with the keyboard and screen readers.

---

## Những điểm mới trong phiên bản này (Tiếng Việt - vi-VN)

### Điểm nổi bật trong bản v2.1.0:
- **Giao diện mới.** Thanh bên kiểu Mac có nhãn (thu gọn còn biểu tượng), thẻ kính mờ, và bố cục dạng ô ở trang chủ, trang Công cụ, Cài đặt và màn hình đổi tên (hiện số tệp sẽ đổi, không đổi, có vấn đề theo thời gian thực). Dùng được ở chế độ sáng và tối, cả ba bảng màu nhấn, và chế độ tương phản cao.
- **Giảm hiệu ứng.** Công tắc mới trong Cài đặt tắt nền mờ, đốm màu và bóng đổ. Công tắc tự bật trên máy không có tăng tốc đồ hoạ (một số máy ảo, Remote Desktop), nơi hiệu ứng làm ứng dụng chậm rõ rệt.
- **Khôi phục lần đổi tên bị gián đoạn.** Nếu ứng dụng bị đóng hoặc lỗi giữa lúc áp dụng đổi tên, lần mở sau sẽ báo cho bạn và lần đó hiện trong Lịch sử với trạng thái "Bị gián đoạn", để bạn hoàn tác và lấy lại tên tệp ban đầu. Không có gì bị đổi cho tới khi bạn chọn Hoàn tác.
- **Nhanh hơn nhiều với danh sách lớn.** Mở 20.000 tệp từng làm cửa sổ đứng hình hơn 10 giây, 100.000 tệp thì hàng phút. Nay 100.000 tệp nạp trong vài giây.
- **Ứng dụng chạy tiếp sau một lỗi màn hình bất ngờ.** Bạn thấy thông báo ngắn, tệp không bị ảnh hưởng, chi tiết ghi vào nhật ký lỗi. Lỗi lặp lại liên tục vẫn dừng ứng dụng một cách an toàn.
- **Ổn định hơn khi chịu tải.** Gia cố với tệp đang bị khoá hoặc chỉ đọc, tên tệp dài hoặc đặc biệt, tệp thay đổi trong lúc bạn làm việc, mở nhiều lần cùng lúc từ Explorer, và mạng chậm hoặc mất khi kiểm tra cập nhật.
- **Tiếp cận.** Các ô ở trang chủ và trang Công cụ nay kích hoạt được bằng bàn phím và trình đọc màn hình.

---

## Ghi chú nội bộ (không gửi lên Partner Center)

**Kiểm thử đã làm cho bản này** (xem `docs/Plan/BFR_CRASH_HANG_TEST_PLAN.md`): fault injection tại mọi điểm ghi đĩa, fuzz 100 000 vòng cho các điểm nhận dữ liệu không đáng tin, bố cục ở 18 tổ hợp giao diện, đi qua mọi trang bằng UI Automation ở 5 cấu hình, kill giữa lúc Áp dụng, tải 100 000 tệp, chaos 10 phút, mạng/Store hỏng, shell extension, hook chuột.

**Lỗi dữ liệu đã sửa:** sau khi app chết giữa lúc Áp dụng, trước đây không có lối Hoàn tác từ giao diện (dữ liệu không mất nhờ journal ghi trước); lỗi ghi dòng "done" của journal làm Hoàn tác bỏ sót bước.

**Chưa kiểm chứng tại chỗ (cần gói MSIX có package identity):** bản sửa luồng mua Pro (gắn cửa sổ chủ cho `StoreContext` trước `RequestPurchaseAsync`). Phải thử trên gói flight/sideload trước khi gửi Store.
