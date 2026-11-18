BFR SOAK KIT: chạy soak test 8 giờ trên VPS Windows
=====================================================

Gói gồm:
  app\                   BatchFileRenamer.exe bản Release, tự chứa .NET (không cần cài .NET trên VPS)
  Stability\             các script kiểm thử (soak, stress-theme, load, chaos)
  run-soak.ps1           khởi chạy soak, trả quyền điều khiển ngay
  restore-power.ps1      hoàn lại cấu hình nguồn sau khi xong

YÊU CẦU VPS
  - Windows có phiên desktop tương tác qua RDP. Khuyên dùng Windows Server 2019/2022 hoặc Windows 10/11.
  - Windows Server: ngắt RDP (Disconnect) vẫn giữ phiên chạy. OK.
  - Windows 10/11: ngắt RDP có thể KHOÁ phiên, và UI Automation ngừng chạy khi desktop bị khoá. Dùng Windows Server,
    hoặc tắt tính năng khoá khi ngắt kết nối (Local Group Policy: Computer Config > Admin Templates > Windows Components >
    Remote Desktop Services > Session Time Limits), hoặc để cửa sổ RDP mở suốt.
  - Cấu hình thấp nhất: 2 vCPU, 4 GB RAM. Không cần GPU (app tự dùng kết xuất phần mềm).
  - Ổ đĩa trống > 2 GB.

CÁC BƯỚC
  1. Copy file soak-kit.zip lên VPS (kéo thả qua RDP hoặc scp), giải nén vào ví dụ C:\bfr-soak\
     (Có thể Windows chặn file tải về: chuột phải zip > Properties > Unblock trước khi giải nén.)
  2. Mở PowerShell (KHÔNG cần Administrator) trong phiên RDP, rồi:
         cd C:\bfr-soak
         .\run-soak.ps1 -DurationMinutes 30      # thử 30 phút trước cho chắc
         .\run-soak.ps1                          # rồi chạy 8 giờ (480 phút)
  3. Theo dõi (tuỳ chọn):
         Get-Content .\results\soak-console.log -Tail 5 -Wait
     Mỗi dòng: giờ, Private MB, Handle, GDI, USER, độ trễ UI (ms), số vòng Áp dụng/Hoàn tác toàn vẹn.
  4. Ngắt RDP bằng cách đóng cửa sổ RDP (Disconnect). ĐỪNG chọn Sign out.
  5. Sau khi xong (8 giờ), mở lại RDP, lấy thư mục results\ về máy: soak-report.md, soak-samples.csv,
     gcdump-*.gcdump (nếu cài dotnet-gcdump), bfr-hang.txt / bfr-crash.txt (chỉ có khi có lỗi).
  6. Chạy .\restore-power.ps1 để hoàn lại cấu hình nguồn.

TIÊU CHÍ ĐẠT (xem trong soak-report.md)
  - Private Bytes tăng < 20% sau khởi động; Handle tăng < 30%; GDI và USER < 5000.
  - Không có mục [hang] của watchdog (UI không treo > 5 giây); không có mục trong bfr-crash.txt.
  - Mọi vòng Áp dụng/Hoàn tác đều khôi phục ĐÚNG tập tên tệp gốc.
  - Lỗi "rename-cycle ... ngôn ngữ vi": là lỗi đang điều tra; nhật ký chi tiết nằm trong báo cáo.

AN TOÀN
  - Script chỉ thao tác trên thư mục tạm do nó tự tạo và chỉ dùng UI Automation (không chuột/bàn phím).
  - Script sao lưu rồi khôi phục %LOCALAPPDATA%\BatchFileRenamer; trên VPS sạch thì không có gì để mất.
  - Không dùng được kèm BatchFileRenamer đang mở thật trên cùng VPS: script từ chối chạy nếu thấy.

SỰ CỐ THƯỜNG GẶP
  - "Không thấy cửa sổ chính": VPS chưa có desktop tương tác (đang ở session dịch vụ), hoặc phiên bị khoá. Đăng nhập lại RDP.
  - Phát hiện lỗi hàng loạt đồng thời từ một thời điểm: thường do phiên bị khoá hoặc VPS bị tạm dừng; xem mốc giờ trong report.
