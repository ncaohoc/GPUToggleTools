# Phát hành bản mới (release)

Ghi chú cho người duy trì repo. Người dùng chỉ cần link tải ở đầu [README](README.md).

Cần có `git` và GitHub CLI đã đăng nhập (`gh auth login`). Commit hết thay đổi rồi chạy:

```bat
powershell -ExecutionPolicy Bypass -File release.ps1
```

Script `release.ps1` làm trọn một lần phát hành:

1. Kiểm tra đang ở `main`, không còn thay đổi chưa commit, không bị tụt so với `origin/main`, và `gh` đã đăng nhập.
2. Tăng version trong `VERSION`: mặc định tăng bản vá `1.2.3 → 1.2.4`. Dùng `-Bump minor` để tăng thành `1.3.0`, `-Bump major` để tăng thành `2.0.0`, hoặc `-Version 2.0.0` để đặt số cụ thể. Nếu số trong `VERSION` chưa phát hành lần nào, script dùng luôn số đó.
3. Build, rồi đóng gói vào thư mục `release/` (`GpuToggle.exe` và `GpuToggle-vX.Y.Z.zip`).
4. Commit `Release vX.Y.Z`, tạo tag `vX.Y.Z`, push `main` và tag.
5. Tạo GitHub Release, đính kèm exe và zip. Ghi chú thay đổi lấy từ các commit kể từ bản trước, hoặc tự viết bằng `-Notes "..."`. Thêm `-Draft` nếu muốn xem lại trước khi công khai.

Thêm `-DryRun` để chỉ tăng version, build và đóng gói thử, không commit hay push gì. Link tải ở đầu [README](README.md) luôn trỏ tới exe của bản mới nhất.
