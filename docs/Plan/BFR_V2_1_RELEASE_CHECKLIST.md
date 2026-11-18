# v2.1.0: checklist phát hành

Nhánh: `feat/v2.1-glass-bento`. Phiên bản đã nâng lên `2.1.0` (`Directory.Build.props`, `AppVersion.cs`, `AppxManifest.xml` = `2.1.0.0`, `build-msix.ps1`). Release notes: `docs/RELEASE_NOTES_v2.1.0.md`. Nội dung "What's new" cho Store: `docs/STORE_WHATS_NEW_v2.1.md`. Hướng dẫn Partner Center của v2.0 (`docs/PARTNER_CENTER_SUBMISSION_v2.0.md`) áp dụng lại được, không đổi giá, không đổi add-on.

## Đã xong (tự động)
- [x] Giao diện mới (glass + bento) mọi trang; công tắc Giảm hiệu ứng; tự tắt kính khi không có tăng tốc đồ hoạ.
- [x] Kiểm thử T0–T5 (xem `BFR_CRASH_HANG_TEST_PLAN.md`): toàn bộ test tự động xanh, gồm 5 cấu hình giao diện × mọi trang, kill giữa lúc Áp dụng, tải 100 000 tệp, chaos, mạng/Store hỏng, shell, hook.
- [x] Sửa lỗi dữ liệu: lần Áp dụng bị gián đoạn nay hoàn tác được từ Lịch sử.
- [x] Gói MSIX và zip portable 2.1.0 dựng được (`artifacts/`, chưa ký).

## Điều kiện chặn phát hành (mục 5 của kế hoạch test), còn lại cho người chạy tay
- [ ] **Mua Pro trong gói Store** (V10): bản sửa gắn cửa sổ chủ cho `StoreContext` chưa kiểm được tại chỗ. Thử trên gói MSIX có package identity (sideload ký thử hoặc flight của Partner Center) trước khi gửi bản chính thức.
- [ ] **Soak 8 giờ** trên VM/VPS Windows (`tests/Stability/kit`, xem `README.txt`). Bản rút gọn 20 phút trên GitHub đã PASS.
- [ ] Checklist VM **V1–V9** (Win10, RDP, DPI 125/150/200 %, mất điện thật, ổ mạng, OneDrive, USB, đăng xuất, hai người dùng) và **V11–V16** (Store ngoại tuyến, phím tắt thật, lắc chuột thật, chuột 4–8 kHz, kill khi hook bật, Explorer 1 giờ).
- [ ] Xem bằng mắt các trang ở chế độ sáng (Presets, Watch, Lịch sử) và bảng kết quả công cụ khi có dữ liệu.

## Việc phát hành (người có quyền Partner Center)
1. Gộp `feat/v2.1-glass-bento` vào `main` (qua Pull Request, chờ CI xanh).
2. Ký gói MSIX bằng chứng chỉ của Store (hoặc để Partner Center ký khi nộp); build lại trên `main` sạch.
3. Cập nhật ảnh chụp Store nếu muốn thể hiện giao diện mới (`docs/store-assets`).
4. Partner Center: tạo bản gửi mới, tải gói, dán "What's new" (`STORE_WHATS_NEW_v2.1.md`), giữ nguyên giá và add-on.
5. Tạo tag `v2.1.0` và GitHub Release kèm zip portable.
