# GPU Toggle Tools

Công cụ WinForms nhỏ giúp **bật/tắt nhanh GPU khi dùng eGPU**: ví dụ tắt iGPU để máy chỉ chạy bằng eGPU, hoặc tắt eGPU trước khi rút dây.

![icon](app.ico)

## Giao diện

Cửa sổ có thanh tiêu đề màu navy kèm logo, và 2 trang chuyển qua lại bằng nút trên thanh tiêu đề:

- **GPUs**: bảng card đồ họa và bảng màn hình. Trạng thái hiện dạng nhãn màu (xanh = Enabled, xám = Disabled, đỏ = Error), cột Load có thanh tải.
- **Apps / Games**: chọn GPU cho từng game hoặc ứng dụng nặng (xem bên dưới).

## Tính năng

### Chọn GPU cho từng game / ứng dụng (trang Apps / Games)

- Phần đầu trang cho biết Windows đang coi card nào là **Power saving** (thường là iGPU) và card nào là **High performance** (thường là eGPU).
- Thêm game bằng các cách sau:
  - **Add app...**: chọn file `.exe`, chọn được nhiều file cùng lúc.
  - **Add running app...**: chọn trong danh sách ứng dụng đang mở.
  - Kéo thả file `.exe` vào danh sách.
- Chọn một hoặc nhiều dòng rồi bấm **Let Windows decide**, **Power saving** hoặc **High performance**. Cài đặt được ghi vào đúng chỗ của *Settings → System → Display → Graphics* (`HKCU/Software/Microsoft/DirectX/UserGpuPreferences`), nên Windows Graphics settings cũng thấy được. Thay đổi có hiệu lực từ **lần mở app tiếp theo**.
- **Launch to test** (hoặc double-click vào dòng) mở game với quyền thường, không phải quyền admin. Cột **Running on** cho biết game đang thật sự chạy trên card nào, kèm % tải, cập nhật mỗi 2 giây. Nhờ vậy bạn kiểm tra được ngay cài đặt có tác dụng hay không.
- **Remove** xoá cài đặt riêng, trả game về lựa chọn mặc định của Windows. **Windows Graphics settings** mở trang cài đặt gốc của Windows.

> Lưu ý: game chạy qua launcher (Steam, Epic…) thì nên thêm file `.exe` của chính game, không phải file của launcher. Nếu game đang chạy thì dùng **Add running app...** cho nhanh.

### Bật/tắt GPU (trang GPUs)

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
