# GPU Toggle Tools

Công cụ WinForms nhỏ giúp **bật/tắt nhanh GPU khi dùng eGPU**: ví dụ tắt iGPU để máy chỉ chạy bằng eGPU, hoặc tắt eGPU trước khi rút dây.

![icon](app.ico)

## Tính năng

- **Tự quét GPU** qua WMI. App liệt kê tên, hãng (NVIDIA/AMD/Intel), trạng thái (Enabled / Disabled / Error) và Instance ID. Adapter ảo (Remote Display…) mặc định bị ẩn, muốn xem thì tick **Show virtual adapters**.
- **Bật/tắt GPU**: chọn GPU rồi bấm **Enable** / **Disable**, hoặc double-click vào dòng để đảo trạng thái. App dùng `pnputil` của Windows.
- **Tự làm mới** khi cắm/rút eGPU (bắt sự kiện `WM_DEVICECHANGE`).
- **Theo dõi thông số** mỗi 2 giây:
  - Load và VRAM cho mọi GPU (dùng Performance Counter của Windows và DXGI).
  - Nhiệt độ, công suất / TDP, quạt (RPM qua NVAPI, nếu không được thì hiện %) cho card NVIDIA (dùng `nvidia-smi`).
  - Nhiệt độ đổi sang cam khi ≥ 75 °C và đỏ khi ≥ 83 °C.
- **Bảng màn hình**: màn hình nào đang được card nào xuất hình, kèm độ phân giải @ Hz và màn hình chính.
- **Chốt an toàn**:
  - Khi tắt GPU vật lý cuối cùng còn chạy, app hiện cảnh báo, nút mặc định là **No**. Windows sẽ chuyển sang Microsoft Basic Display Adapter.
  - Sau khi tắt, app mở hộp đếm ngược **15 giây**. Nếu không bấm **Keep**, GPU tự bật lại, nên bạn không bị kẹt khi màn hình đen.

## Yêu cầu

- Windows 10 bản 2004 trở lên, hoặc Windows 11 (`pnputil /enable-device`).
- .NET Framework 4.x, có sẵn trong Windows.
- Quyền Administrator: app tự xin quyền khi mở.
- Phần thông số NVIDIA cần driver NVIDIA, trong đó có `nvidia-smi` và `nvapi64.dll`.

## Build

Không cần Visual Studio hay .NET SDK. Để các file `GpuToggle.cs`, `app.manifest`, `app.ico`, `build.bat` chung một thư mục rồi chạy:

```bat
build.bat
```

Script dùng `csc.exe` có sẵn trong `%SystemRoot%/Microsoft.NET/Framework64/v4.0.30319/` và tạo ra `GpuToggle.exe`. Build xong chỉ cần giữ lại file `.exe`.

> Mã nguồn cố ý không dùng ký tự `\`, vì ký tự này có thể bị mất khi tải file về.

## Lưu ý khi dùng eGPU

- **Tắt iGPU làm màn hình gắn eGPU cũng tắt?** Trước khi tắt iGPU, bấm `Win + P` và chọn **Second screen only**. Bạn cũng nên kiểm tra ở *Settings → System → Display → Advanced display* rằng màn hình ngoài đang *Connected to* card eGPU. Nếu nó hiện iGPU, hãy cắm dây thẳng vào cổng trên card.
- Màn hình đen: thử `Win + Ctrl + Shift + B`, rút ra cắm lại dây, hoặc chờ 15 giây để app tự bật lại GPU.
- Nên tắt eGPU trong app trước khi rút dây eGPU.
