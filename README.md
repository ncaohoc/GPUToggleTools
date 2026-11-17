# GPU Toggle Tools

Công cụ WinForms nhỏ giúp **bật/tắt nhanh GPU khi dùng eGPU**: ví dụ tắt iGPU để máy chỉ chạy bằng eGPU, hoặc tắt eGPU trước khi rút dây.

![icon](app.ico)

## Giao diện

Cửa sổ có thanh tiêu đề màu navy kèm logo, và 4 trang chuyển qua lại bằng nút trên thanh tiêu đề:

- **GPUs**: bảng card đồ họa và bảng màn hình. Trạng thái hiện dạng nhãn màu (xanh = Enabled, xám = Disabled, đỏ = Error), cột Load có thanh tải.
- **Apps / Games**: chọn GPU cho từng game hoặc ứng dụng nặng (xem bên dưới).
- **PCIe monitor**: đồ thị băng thông PCIe thời gian thực khi chơi game, kèm nhật ký những lần bị nghẽn.
- **OSD**: lớp thông số hiện đè lên game (FPS, GPU, VRAM, CPU, RAM, PCIe) và cho biết cái gì đang giới hạn FPS.
- **Dark mode**: nút **Theme** ở góc phải thanh tiêu đề đổi lần lượt *Auto* (theo cài đặt sáng/tối của Windows) → *Light* → *Dark*. Đổi ngay không cần mở lại app, lựa chọn được lưu lại (`HKCU/Software/GPUToggle`).

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

### Đo băng thông PCIe (nút **PCIe bandwidth...** trên trang GPUs)

- Đo tốc độ chép dữ liệu thật giữa RAM và VRAM theo cả 2 chiều, **Host -> GPU** và **GPU -> Host**. App dùng Direct3D 11 (chép các khối 64 MB, bấm giờ bằng timestamp của GPU) nên chạy được với mọi hãng GPU.
- Hiện link PCIe đang chạy (vd. `Gen4 x4`), mức link card hỗ trợ, băng thông lý thuyết và kết quả đo đạt bao nhiêu % so với lý thuyết.
- Tự nhận ra eGPU cắm qua **USB4 / Thunderbolt** (ASMedia ASM2464, chip Thunderbolt của Intel). Với các eGPU này, đường hầm USB4/Thunderbolt thường mới là giới hạn thật, chứ không phải link PCIe mà Windows báo.

### Theo dõi PCIe khi chơi game (trang PCIe monitor, chỉ card NVIDIA)

- Bấm **Start recording** rồi mở game. Nếu card chưa được đo băng thông thực tế, app bắt chạy bài đo trước (vài giây) rồi mới bắt đầu ghi. App đọc lưu lượng PCIe 4 lần mỗi giây qua NVML (`nvml.dll`, có sẵn trong driver NVIDIA), kể cả khi cửa sổ app bị thu nhỏ.
- **Đồ thị thời gian thực** 2 phút gần nhất cho 2 chiều upload / download, tính theo % giới hạn link. Vùng tô đỏ là lúc bị nghẽn.
- Các thẻ số liệu:
  - **Link limit**: băng thông đo thực tế (GB/s và % của link) cạnh băng thông lý thuyết = tốc độ mỗi lane của chuẩn PCIe đang chạy × số lane, ví dụ Gen4 x4 = 1,97 × 4 = 7,88 GB/s.
  - **Now**: lưu lượng hiện tại.
  - **Average**: lưu lượng trung bình cả phiên.
  - **Peak**: lưu lượng cao nhất, kèm thời điểm đạt được.
  - **Bottleneck time**: tổng thời gian nghẽn, bao nhiêu % thời lượng phiên và số lần nghẽn.
- **Bottleneck log**: ghi lại mọi lần nghẽn dài từ 0,5 giây trở lên, gồm giờ bắt đầu, thời lượng, chiều nghẽn, mức đỉnh và ứng dụng đang mở ở cửa sổ trước.
- Mốc 100 % là băng thông lý thuyết của link. Ngưỡng nghẽn được tự hạ xuống đúng mức đo thực tế, ví dụ eGPU Gen4 x4 qua USB4 đo được 3,8 GB/s trên 7,9 GB/s lý thuyết thì ngưỡng là 48 %. Vẫn chỉnh tay được.
- **Measure limit...** chạy lại bài đo và đặt lại ngưỡng nghẽn theo kết quả mới. Trong lúc bài đo chạy, app tạm dừng ghi.
- **Export CSV...** xuất file CSV gồm số liệu tổng hợp, danh sách các lần nghẽn và toàn bộ mẫu đo theo thời gian.

> NVML đếm lưu lượng thô trên link của card, có cả phần overhead của giao thức, nên con số GB/s ở đây thường cao hơn kết quả bài đo. Hãy xem cột % để đánh giá mức nghẽn. Card AMD/Intel không cho ứng dụng đọc bộ đếm lưu lượng PCIe, nên với các card này chỉ dùng được bài đo băng thông.

### OSD trong game (trang OSD)

- Bấm **Show OSD**, hoặc nhấn **Ctrl+Shift+O** ngay trong game, để bật/tắt bảng thông số ở góc màn hình. Bảng này không nhận chuột hay bàn phím (click xuyên qua) và không bao giờ cướp focus của game.
- Các dòng hiện trên OSD (chọn được từng dòng):
  - **FPS**: số khung hình/giây, thời gian mỗi khung (ms) và **1 % low** trong 10 giây gần nhất.
  - **GPU**: tải %, nhiệt độ, công suất (W). Chuyển màu cam/đỏ khi gần chạm giới hạn công suất hoặc nóng.
  - **VRAM**: dung lượng đã dùng / tổng.
  - **CPU**: tải tổng, **luồng bận nhất** (`max`) và nhiệt độ CPU.
  - **RAM**: dung lượng đã dùng / tổng.
  - **PCIe**: lưu lượng ↑ Host -> GPU và ↓ GPU -> Host (GB/s), kèm % so với giới hạn đo thực tế (chỉ card NVIDIA).
  - **LIM**: thứ đang giới hạn FPS, tính trung bình 3 giây gần nhất.
- Chọn được GPU cần theo dõi. Mặc định **Auto** là card mà game ở cửa sổ trước đang chạy. Chọn được góc màn hình, cỡ chữ và độ đậm của nền. Mọi lựa chọn được lưu lại.
- Trang OSD hiện các thẻ số liệu giống hệt OSD, cộng thêm thẻ **What limits the FPS** giải thích chi tiết:

| Kết luận | Điều kiện | Nên làm gì |
|---|---|---|
| **GPU-bound** | GPU ≥ 95 % | Bình thường. Muốn thêm FPS thì giảm độ phân giải / thiết lập, hoặc bật DLSS / FSR. |
| **CPU-bound** | Luồng CPU bận nhất ≥ 90 %, GPU < 95 % | GPU đang chờ CPU. Tăng độ phân giải không mất FPS; giảm tầm nhìn xa, mật độ NPC, vật lý. |
| **PCIe link** | Lưu lượng ≥ 90 % giới hạn đo thực tế | Thường gặp với eGPU. Giảm texture / độ phân giải, cắm màn hình thẳng vào eGPU. |
| **VRAM full** | VRAM ≥ 95 % | Texture tràn sang RAM và phải đi qua PCIe nên bị giật. Giảm chất lượng texture. |
| **V-Sync / refresh cap** | FPS bằng tần số quét màn hình | Game đang bị khóa FPS, không có gì bị nghẽn. |
| **Limited elsewhere** | Không có gì chạm ngưỡng | Do giới hạn FPS trong game, engine, hoặc một luồng game nhảy qua lại giữa các nhân. |

> - FPS được đọc từ sự kiện present mà Windows tự ghi lại (ETW, giống PresentMon), app **không chèn gì vào game** nên an toàn với anti-cheat. Đo được DirectX 9 – 12; Vulkan / OpenGL được đếm qua sự kiện đồ họa của kernel. App đo FPS của **cửa sổ đang ở trước**. Nếu bạn chuyển sang cửa sổ GPU Toggle để xem trang OSD, app vẫn giữ game vừa chơi.
> - OSD là cửa sổ luôn nằm trên cùng, nên hiện được trên game chạy **cửa sổ** hoặc **borderless**. Game chạy *exclusive fullscreen* có thể che mất OSD, khi đó hãy chuyển game sang borderless.
> - Windows không có API chuẩn cho nhiệt độ CPU. App đọc từ **LibreHardwareMonitor** (hoặc OpenHardwareMonitor) nếu chương trình đó đang chạy, nếu không thì dùng *ACPI thermal zone* (chỉ gần đúng, một số máy không có). Nhiệt độ GPU AMD cũng chỉ có khi LibreHardwareMonitor đang chạy.
> - Để % PCIe chính xác, hãy chạy **PCIe bandwidth...** một lần cho card. Nếu chưa đo thì 100 % là băng thông lý thuyết của link.
> - Nếu Ctrl+Shift+O đã bị app khác chiếm, dòng thông tin trên trang OSD sẽ báo, khi đó dùng nút **Show OSD**.

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

Không cần Visual Studio hay .NET SDK. Để các file `GpuToggle.cs`, `Osd.cs`, `app.manifest`, `app.ico`, `build.bat` chung một thư mục rồi chạy:

```bat
build.bat
```

Script dùng `csc.exe` có sẵn trong `%SystemRoot%/Microsoft.NET/Framework64/v4.0.30319/` và tạo ra `GpuToggle.exe`. Build xong chỉ cần giữ lại file `.exe`.

> Mã nguồn cố ý không dùng ký tự `\`, vì ký tự này có thể bị mất khi tải file về.

## Lưu ý khi dùng eGPU

- **Tắt iGPU làm màn hình gắn eGPU cũng tắt?** Trước khi tắt iGPU, bấm `Win + P` và chọn **Second screen only**. Bạn cũng nên kiểm tra ở *Settings → System → Display → Advanced display* rằng màn hình ngoài đang *Connected to* card eGPU. Nếu nó hiện iGPU, hãy cắm dây thẳng vào cổng trên card.
- Màn hình đen: thử `Win + Ctrl + Shift + B`, rút ra cắm lại dây, hoặc chờ 15 giây để app tự bật lại GPU.
- Nên tắt eGPU trong app trước khi rút dây eGPU.
