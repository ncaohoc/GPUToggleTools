# GPU Toggle Tools

Công cụ WinForms nhỏ giúp **bật/tắt nhanh GPU khi dùng eGPU**: ví dụ tắt iGPU để máy chỉ chạy bằng eGPU, hoặc tắt eGPU trước khi rút dây.

**Vì sao có tool này:** người dùng eGPU nên tập thói quen *Disable + rút eGPU trước khi tắt máy*, rồi *cắm eGPU + Enable sau khi đã vào Windows*. Làm vậy thì tránh được lỗi eGPU (Code 43, màn hình đen) và tránh được cảnh Windows phát hiện phần cứng thay đổi rồi **bắt đặt lại PIN mỗi lần khởi động**. Trước đây mỗi lần như vậy phải mở Device Manager. GPU Toggle làm việc này bằng một cú double-click, kèm chốt an toàn tự bật lại card sau 15 giây nếu màn hình bị đen. Chi tiết ở phần [Lưu ý khi dùng eGPU](#lưu-ý-khi-dùng-egpu).

![icon](app.ico)

**Tải về:** [GpuToggle.exe bản mới nhất](https://github.com/ncaohoc/GPUToggleTools/releases/latest/download/GpuToggle.exe). Các bản cũ và ghi chú thay đổi nằm ở trang [Releases](https://github.com/ncaohoc/GPUToggleTools/releases). Tải xong mở luôn, không cần cài đặt.

## Giao diện

Cửa sổ có thanh tiêu đề màu navy kèm logo, và 5 trang chuyển qua lại bằng nút trên thanh tiêu đề:

- **GPUs**: bảng card đồ họa và bảng màn hình. Trạng thái hiện dạng nhãn màu (xanh = Enabled, xám = Disabled, đỏ = Error), cột Load có thanh tải.
- **Apps / Games**: chọn GPU cho từng game hoặc ứng dụng nặng (xem bên dưới).
- **PCIe monitor**: đồ thị băng thông PCIe thời gian thực khi chơi game, kèm nhật ký những lần bị nghẽn.
- **OSD**: lớp thông số hiện đè lên game (FPS, GPU, VRAM, CPU, RAM, PCIe) và cho biết cái gì đang giới hạn FPS.
- **Repair**: nhóm công cụ sửa lỗi: gỡ sạch driver NVIDIA / AMD và tự sửa lỗi Code 43 (xem bên dưới).
- **Dark mode**: công tắc mặt trời / mặt trăng ở góc phải thanh tiêu đề. Lúc chưa bấm, app theo chế độ sáng/tối của Windows; đã bấm thì app nhớ lựa chọn (`HKCU/Software/GPUToggle`). Đổi ngay không cần mở lại app.

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
- **Graph next to**: bật/tắt đồ thị nhỏ (~10 giây gần nhất) nằm ngay trong hàng cho từng thông số. Mặc định bật cho FPS và PCIe.
  - FPS: đồ thị tự co giãn theo FPS cao nhất, nên lần tụt FPS hay khựng hình hiện thành một cái hố.
  - GPU / VRAM / RAM: thang cố định 0 – 100 %. CPU: xanh là tải tổng, hồng là luồng bận nhất.
  - PCIe: xanh là ↑ Host -> GPU, hồng là ↓ GPU -> Host, chung một thang.
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

### Sửa lỗi (trang Repair)

Các công cụ này can thiệp sâu vào hệ thống nên được gom riêng một trang, mọi bước đều ghi vào ô **Log** ở cuối trang.

**Driver cleanup: gỡ sạch driver NVIDIA / AMD** (giống DDU)

- Chọn **NVIDIA** hoặc **AMD** (app tự chọn theo card đang có) rồi bấm **Remove driver...**. App quét trước và ghi ra Log toàn bộ những gì sẽ xoá, hỏi lại rồi mới làm:
  1. Tạo restore point (tick **Create a restore point first**; Windows chỉ tạo tối đa 1 điểm mỗi 24 giờ).
  2. Tắt các chương trình và service của hãng (NVIDIA App, NVDisplay.Container, AMD External Events...).
  3. Xoá mọi gói driver của hãng khỏi driver store (`pnputil /delete-driver /uninstall /force`): driver màn hình, âm thanh HDMI, USB-C, Virtual Audio...
  4. Xoá service, gỡ thiết bị (GPU và âm thanh HDMI của nó).
  5. Xoá thư mục còn sót, cài đặt, shader cache và mục gỡ cài đặt trong Control Panel (tick **Also delete leftover folders...**). File đang bị khoá sẽ được xoá ở lần khởi động sau.
  6. Quét lại phần cứng: GPU quay về *Microsoft Basic Display Adapter*. Khởi động lại rồi cài driver mới.
- Với AMD, app chỉ xoá phần của card Radeon (driver màn hình, âm thanh HDMI, Crash Defender, AMD Software). **Driver chipset AMD không bị đụng tới.**
- **Stop Windows Update from installing drivers**: chặn Windows Update tự cài lại driver cũ ngay sau khi gỡ. Có hiệu lực ngay khi tick, bỏ tick để cho phép lại.
- **Restart to Safe Mode...**: khởi động vào Safe Mode **một lần** (cách gỡ sạch nhất vì không driver GPU nào được nạp). Mở lại GPU Toggle trong Safe Mode, app tự mở trang Repair. Lần khởi động sau tự về chế độ thường, không bị kẹt trong Safe Mode. App dùng một mục boot tạm (`bcdedit /bootsequence`) và tự xoá mục này khi về lại chế độ thường.

> **Quan trọng trước khi vào Safe Mode: đăng nhập bằng PIN**
> - Safe Mode **không có mạng** và thường **không nhận PIN / Windows Hello**, bạn phải nhập **mật khẩu tài khoản**. Với tài khoản Microsoft, đó là mật khẩu Microsoft (máy phải từng đăng nhập bằng mật khẩu này).
> - Nếu Windows bắt đặt lại PIN khi khởi động (thường do phát hiện phần cứng thay đổi, ví dụ vừa cắm hoặc rút eGPU), bạn **không làm được trong Safe Mode** vì cần mạng để xác minh.
> - Nếu bị kẹt ở màn hình đăng nhập: bấm biểu tượng nguồn ở góc màn hình đăng nhập → **Restart**. Vì app chỉ đặt Safe Mode cho **một lần** khởi động, máy sẽ quay về chế độ thường. Tránh dùng cách bật Safe Mode bằng `msconfig`: cách đó lặp lại mãi đến khi tắt được.
> - Không chắc nhớ mật khẩu? Đừng vào Safe Mode, cứ bấm **Remove driver...** ngay ở chế độ thường (vẫn gỡ được, chỉ kém sạch hơn một chút).

**Fix code 43**

- Ô trạng thái cho biết GPU nào đang bị Windows dừng với lỗi **Code 43**. Bấm **Fix code 43**, app thử từ nhẹ đến mạnh và dừng ngay khi GPU chạy lại:
  1. Khởi động lại thiết bị (`pnputil /restart-device`, hoặc tắt rồi bật).
  2. Gỡ thiết bị rồi quét lại phần cứng để Windows cài lại từ đầu.
  3. Xoá các adapter "ma" (eGPU từng cắm ở cổng khác) rồi quét lại.
  4. **Chỉ dành cho người dùng eGPU** (tick **I use an eGPU**, card NVIDIA): ghi `RM1774520 = 1` (DWORD) vào khoá driver của card (`HKLM/SYSTEM/CurrentControlSet/Control/Class/{4d36e968-e325-11ce-bfc1-08002be10318}/NNNN`) rồi khởi động lại card. Đây là cách sửa lỗi 43 cho card NVIDIA cắm qua hộp eGPU, lấy từ [nvidia-error43-fix](https://github.com/AleCyriaco/nvidia-error43-fix) (MIT), dựa trên nvidia-error43-fixer của nando4 (egpu.io). NVIDIA không công bố tài liệu cho giá trị này.
  5. Cũng chỉ khi tick ô eGPU: tắt Fast Startup và PCIe Link State Power Management (có tác dụng sau khi khởi động lại).
- Ô **I use an eGPU** mặc định không tick, app tự tick khi card lỗi nằm sau chip USB4 / Thunderbolt. Hộp eGPU cắm qua OCuLink hoặc M.2 thì app không nhận ra được, bạn tự tick. Người dùng card gắn trong máy (không phải eGPU) không nên tick.
- Giá trị `RM1774520` mất khi cài lại hoặc cập nhật driver: bấm **Fix code 43** lại. Muốn bỏ thì xoá giá trị `RM1774520` trong khoá driver ở trên (đường dẫn đầy đủ có trong Log), hoặc gỡ card trong Device Manager rồi khởi động lại.
- Nếu vẫn lỗi, Log ghi các bước nên làm tiếp: gỡ sạch driver bằng Driver cleanup rồi cài lại, tập thói quen cắm / rút eGPU đúng lúc (xem [Lưu ý khi dùng eGPU](#lưu-ý-khi-dùng-egpu)), đổi cổng / cáp, bật Thunderbolt PCIe tunneling và Above 4G decoding trong BIOS.

## Yêu cầu

- Windows 10 bản 2004 trở lên, hoặc Windows 11 (`pnputil /enable-device`).
- .NET Framework 4.x, có sẵn trong Windows.
- Quyền Administrator: app tự xin quyền khi mở.
- Phần thông số NVIDIA cần driver NVIDIA, trong đó có `nvidia-smi` và `nvapi64.dll`.

## Build

Không cần Visual Studio hay .NET SDK. Giữ nguyên thư mục `src/` cùng các file `VERSION`, `app.manifest`, `app.ico`, `build.bat` rồi chạy:

```bat
build.bat
```

Script dùng `csc.exe` có sẵn trong `%SystemRoot%/Microsoft.NET/Framework64/v4.0.30319/`, biên dịch mọi file `.cs` trong `src/` và tạo ra `GpuToggle.exe`. Build xong chỉ cần giữ lại file `.exe`. Version lấy từ file `VERSION`: script sinh ra `src/Version.cs` (không commit), nên số version hiện ở tiêu đề cửa sổ và trong *Properties → Details* của file exe.

Mã nguồn chia theo trách nhiệm, mỗi file một class:

| Thư mục | Nội dung |
|---|---|
| `src/Core` | Hằng số chuỗi, định dạng số, đường dẫn registry của app |
| `src/Hardware` | Đọc phần cứng: danh sách GPU và bật/tắt (pnputil), DXGI, perf counter, NVML, nvidia-smi, màn hình, CPU/RAM, cảm biến nhiệt |
| `src/Pcie` | Thông tin link PCIe, bài đo băng thông D3D11, bộ ghi PCIe monitor, xuất CSV |
| `src/Apps` | Lựa chọn GPU cho từng app (registry của Windows Graphics settings) |
| `src/Repair` | Gỡ sạch driver (driver store, service, thư mục), sửa Code 43, khởi động Safe Mode một lần, chặn Windows Update cài driver |
| `src/Osd` | Đếm FPS (ETW), bộ lấy mẫu, đánh giá nghẽn, cài đặt và cửa sổ OSD |
| `src/UI` | `MainForm` (khung cửa sổ, chuyển trang), theme, control tự vẽ, hộp thoại, và mỗi trang một class trong `src/UI/Pages` |

Thêm trang mới: tạo class kế thừa `AppPage` trong `src/UI/Pages` rồi thêm vào danh sách `pages` của `MainForm`.

> Mã nguồn viết theo cú pháp C# 5 (để `csc.exe` có sẵn của Windows biên dịch được), chỉ dùng ASCII và cố ý không dùng ký tự `\`, vì ký tự này có thể bị mất khi tải file về.

## Lưu ý khi dùng eGPU

> **Thói quen nên tập để tránh lỗi Code 43, màn hình đen và việc bị bắt đặt lại PIN** (đây chính là việc GPU Toggle được làm ra để làm nhanh, thay cho Device Manager):
> - **Trước khi tắt máy** (Shut down / Restart / Sleep lâu): vào trang GPUs, chọn eGPU → **Disable**, rồi **rút dây eGPU**.
> - **Sau khi mở máy**: chờ Windows khởi động xong và đăng nhập vào desktop, rồi mới **cắm dây eGPU**, sau đó vào trang GPUs → **Enable** nếu nó đang Disabled.
> - Lý do: khi eGPU có mặt lúc khởi động (nhất là khi Fast Startup đang bật), BIOS / Windows phải cấp tài nguyên PCIe cho card ngay từ đầu. Việc này hay lỗi với hộp USB4 / Thunderbolt và sinh ra Code 43, màn hình đen hoặc adapter "ma". Cắm nóng sau khi vào Windows thì driver nhận card trong trạng thái sạch.
> - Thêm nữa, eGPU xuất hiện hoặc biến mất giữa các lần khởi động có thể khiến Windows coi là phần cứng đã thay đổi và **bắt đặt lại PIN** (Windows Hello) khi đăng nhập. Khi eGPU luôn vắng mặt lúc khởi động, phần cứng lúc boot không đổi nên Windows không hỏi lại.

- **Tắt iGPU làm màn hình gắn eGPU cũng tắt?** Trước khi tắt iGPU, bấm `Win + P` và chọn **Second screen only**. Bạn cũng nên kiểm tra ở *Settings → System → Display → Advanced display* rằng màn hình ngoài đang *Connected to* card eGPU. Nếu nó hiện iGPU, hãy cắm dây thẳng vào cổng trên card.
- Màn hình đen: thử `Win + Ctrl + Shift + B`, rút ra cắm lại dây, hoặc chờ 15 giây để app tự bật lại GPU.
- Nên tắt eGPU trong app trước khi rút dây eGPU.
