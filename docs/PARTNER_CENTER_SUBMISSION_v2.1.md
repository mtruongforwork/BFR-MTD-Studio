# Hướng dẫn nộp bản cập nhật v2.1.0 lên Partner Center

Bản này chỉ là **cập nhật app**. Add-on `BFR_PRO_LIFETIME`, giá, thị trường, độ tuổi và quyền của app **giữ nguyên** như v2.0. Cách điền từng ô xem lại `PARTNER_CENTER_SUBMISSION_v2.0.md`; tài liệu này chỉ liệt kê những gì **khác**.

## Bước 0. Chuẩn bị

| Việc | Chi tiết |
| :--- | :--- |
| Gói | `artifacts/BatchFileRenamer-2.1.0.msix`, chưa ký (Store tự ký). Nên build lại từ `main` sau khi gộp PR #11: `packaging/msix/build-msix.ps1 -Configuration Release`. Gói hiện có: SHA-256 `17abea86…` |
| Ảnh | `docs/store-assets/v2.1/01…08` (PNG 1600×900) và chú thích trong `STORE_LISTING_v2.1.md` |
| Mô tả | `docs/STORE_LISTING_v2.1.md` (vi-VN và en-US) |
| What's new | `docs/STORE_WHATS_NEW_v2.1.md` |
| Phiên bản | `2.1.0.0`, lớn hơn `2.0.0.0` đang phát hành nên hợp lệ |

## Bước 1. Tạo bản gửi
**Apps and games → BFR - Batch File Renamer → Application overview → Update**. Mở bản nháp nếu đã có.

## Bước 2. Những mục cần sửa

| Mục | Làm gì |
| :--- | :--- |
| Pricing and availability | **Không sửa** |
| Properties | **Không sửa** |
| Age ratings | **Không sửa** |
| Packages | Xoá gói `2.0.0.0` khỏi bản gửi nếu Partner Center giữ lại, kéo gói `2.1.0.msix` vào, chờ **Validated**. Có thể bật gradual rollout (ví dụ 20 %) để giảm rủi ro |
| Store listings, vi-VN và en-US | Dán **Description** và **Product features** mới; dán **What's new in this version** từ `STORE_WHATS_NEW_v2.1.md`; **xoá 7 ảnh v2.0**, tải 8 ảnh v2.1 theo thứ tự 01 → 08 kèm chú thích. Mô tả ngắn và từ khoá không đổi |
| Submission options | Thêm đoạn **11** (cuối `STORE_LISTING_v2.1.md`) vào cuối Notes for Certification |

## Bước 3. Nộp
Bấm **Submit for certification**. Không cần nộp add-on.

## Bước 4. Sau khi lên Store (nên làm, vì bước kiểm tra mua Pro đã bỏ qua trước khi nộp)

1. Cập nhật app từ Store trên máy mày, mở thử Trang chủ, Đổi tên, Công cụ.
2. **Kiểm tra mua Pro:** mở hộp thoại Nâng cấp lên Pro, bấm Mua. Hộp thoại thanh toán của Microsoft Store phải hiện ra đè lên cửa sổ app. Với tài khoản đã có Pro, app phải nhận Pro ngay khi mở.
3. Nếu hộp thoại mua không hiện, hoặc báo lỗi: tạm dừng rollout (Packages → Halt) và báo tao, kèm tệp nhật ký lỗi trong `%LOCALAPPDATA%\BatchFileRenamer`.

## Đã biết, chưa sửa trong 2.1
- Trang Cài đặt vẫn hiện ô "Update feed URL" chứa chuỗi giữ chỗ `YOUR-ORG`. Bản Store không dùng ô này (Store tự cập nhật), nên không ảnh hưởng chức năng, chỉ trông chưa hoàn thiện. Bản zip portable vì thế không tự kiểm tra cập nhật được. Lỗi có từ các bản trước.
