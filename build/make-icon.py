# -*- coding: utf-8 -*-
"""从品牌源图生成应用图标（app.ico）。

为什么用脚本生成而不是直接放一个 .ico：图标是从设计稿（logo.jpg）派生的，
中间有「量出图案范围、拟合圆角、补透明圆角、缩到多档」这几步。把处理写成
脚本，源图换了重跑一次即可，同一套处理也能被复核；直接塞一个二进制 .ico
进仓库，则谁也不知道它是怎么从源图变来的、是否与源图一致。

两个产物，出自同一次量取，因此界面上的品牌标记与系统里的图标必然同形：

- src\\QYPlayer.App\\Assets\\app.ico
  被 QYPlayer.App.csproj 以 ApplicationIcon 引用，决定了 exe、任务栏、窗口、
  快捷方式与文件关联的默认图标；同时被 build\\QYPlayer.iss 的 SetupIconFile 引用，
  因此安装程序与卸载项也用它。
- src\\QYPlayer.App\\Assets\\app-logo.png
  供标题栏使用（MainWindow.xaml 的 TitleBar.Icon，经 {ui:ImageIcon} 引入）。
  标题栏要的是位图而非 .ico：WPF 的 ImageIcon 只认位图源，喂 .ico 会落到
  系统图标解码器上，取到哪一档不可控。

依赖 Pillow（仅此一处用到的开发期工具，不是构建应用的依赖）。
用法：python build\\make-icon.py
"""

from __future__ import annotations

import sys
from pathlib import Path

try:
    from PIL import Image, ImageChops, ImageDraw
except ImportError:  # pragma: no cover - 环境问题，给出可执行的提示即可
    sys.exit("缺少 Pillow。请先执行：pip install pillow")

REPO_ROOT = Path(__file__).resolve().parent.parent
ASSETS = REPO_ROOT / "src" / "QYPlayer.App" / "Assets"
TARGET = ASSETS / "app.ico"
PNG_TARGET = ASSETS / "app-logo.png"

# 品牌源图的查找顺序。正式位置是 Assets\logo.jpg；把根目录也列进来，
# 是为了「换了新图先丢在根目录」这种顺手做法也能直接重跑，不必先手工挪位置。
SOURCE_CANDIDATES = [
    ASSETS / "logo.jpg",
    ASSETS / "logo.png",
    REPO_ROOT / "logo.jpg",
    REPO_ROOT / "logo.png",
]

# 落进 ico 的档位。除常规的 16/32/48/256，还补了 20/24/40/64/128：
# Windows 在不同 DPI 与不同视图（任务栏、详细列表、大图标、jumbo）下各取所需，
# 缺档时系统要自己缩放，细小笔画容易糊。
SIZES = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40),
         (48, 48), (64, 64), (128, 128), (256, 256)]

# 母图边长上限。只在源图特别大时起作用（限制内存与耗时），正常不会触发；
# 关键是「绝不上采样」——源图案多大就在多大上作业，之后一路做缩小。
MASTER_LIMIT = 2048

# 交给标题栏的位图边长。标题栏按逻辑像素 16 上下显示，高分屏最多放大到两三倍，
# 128 足够覆盖且体积可忽略；再大只是白占内存。
PNG_SIZE = 128

# 判定「属于图案」的两个线索。源图是白底浅蓝图案，两者都很亮，单看亮度分不开，
# 因此补一条「蓝通道高于红通道」的色偏线索。
TINT_THRESHOLD = 4      # 蓝减红超过它即认为有色偏（图案填充是浅蓝）
DARK_THRESHOLD = 240    # 亮度低于它即认为比背景暗（图案上的深蓝字形）
PROFILE_FLOOR = 0.05    # 投影计数低于峰值这个比例的边视为噪点，不计入范围


def load_mask(image: Image.Image) -> Image.Image:
    """把源图压成「图案 vs 背景」的二值掩码。

    背景是略偏暖的白（R 略高于 B），图案是偏冷的浅蓝（B 明显高于 R），
    所以「蓝减红」比亮度更能分开两者——图案顶部接近纯白的地方也能命中。
    """
    red, _green, blue = image.split()[:3]
    tint = ImageChops.subtract(blue, red)               # b - r，负值截为 0
    cool = tint.point(lambda v: 255 if v > TINT_THRESHOLD else 0)
    dark = image.convert("L").point(lambda v: 255 if v < DARK_THRESHOLD else 0)
    return ImageChops.lighter(cool, dark)


def _profile_span(profile: Image.Image) -> tuple[int, int]:
    """在投影上找出图案的起止位置。

    用投影而不是直接取掩码的外接框：JPEG 压缩会在背景里留零星噪点，
    外接框会被一个孤立像素撑到整张图。低于峰值一成的边一律不算。
    """
    # 投影是压到 1 像素后的灰度均值，取值 0..255，与真实计数只差常数倍，
    # 判「相对峰值」够用。tobytes 比逐像素访问快得多。
    values = profile.tobytes()
    peak = max(values)
    if peak <= 0:
        raise SystemExit("源图里没找到图案，请检查品牌源图是否正确。")
    floor = peak * PROFILE_FLOOR
    hits = [i for i, v in enumerate(values) if v > floor]
    return hits[0], hits[-1]


def measure(mask: Image.Image) -> tuple[int, int, int, int]:
    """量出图案的外接矩形（左、上、右、下，含端点）。"""
    width, height = mask.size
    # 按列、按行各压成一条线，每个点的值就是该列/该行的图案像素数。
    columns = mask.resize((width, 1), Image.BOX)
    rows = mask.resize((1, height), Image.BOX)
    left, right = _profile_span(columns)
    top, bottom = _profile_span(rows)
    return left, top, right, bottom


def fit_corner_radius(mask: Image.Image, left: int, top: int, side: int) -> float:
    """用最小二乘圆拟合量出圆角半径。

    为什么不直接找「弧线到哪一行结束」：那个转折点被抗锯齿和 JPEG 振铃糊住，
    目测偏差几十像素很正常。改为取左上角一片区域逐行找最左的图案像素得到边界点，
    再拟合出圆——半径是它到圆心的距离，与「弧线何时变直」无关，稳得多。
    """
    span = int(side * 0.55)
    width, height = mask.size
    box = (left, top, min(left + span, width), min(top + span, height))
    region = mask.crop(box)
    pixels = region.load()
    region_width, region_height = region.size

    # 逐行取最左的图案像素；找到即跳出，内层循环通常只走几十步。
    points: list[tuple[float, float]] = []
    for y in range(region_height):
        for x in range(region_width):
            if pixels[x, y] > 127:
                points.append((float(x), float(y)))
                break

    if len(points) < 20:
        raise SystemExit("可用的圆角边界点太少，源图是否被裁切过？")

    # 所有边界点的最左值即直边位置，以它作基准挑出弧线段。
    straight = min(x for x, _ in points)
    arc = [(x, y) for x, y in points if 2.0 < (x - straight) < side * 0.30]
    if len(arc) < 20:
        raise SystemExit("没能分离出圆角弧线，源图是否本就是直角矩形？")

    # Kasa 圆拟合：把 x^2+y^2+Dx+Ey+F=0 视作对 (D,E,F) 的线性最小二乘。
    # 手写正规方程而不引 numpy，是为了让这个脚本只依赖 Pillow。
    n = float(len(arc))
    sum_x = sum(x for x, _ in arc)
    sum_y = sum(y for _, y in arc)
    sum_xx = sum(x * x for x, _ in arc)
    sum_yy = sum(y * y for _, y in arc)
    sum_xy = sum(x * y for x, y in arc)
    sum_xz = sum(x * (x * x + y * y) for x, y in arc)
    sum_yz = sum(y * (x * x + y * y) for x, y in arc)
    sum_z = sum(x * x + y * y for x, y in arc)

    m = [[sum_xx, sum_xy, sum_x],
         [sum_xy, sum_yy, sum_y],
         [sum_x, sum_y, n]]
    v = [-sum_xz, -sum_yz, -sum_z]

    def det3(a: list[list[float]]) -> float:
        """三阶行列式，用于克拉默法则解 3x3 方程组。"""
        return (a[0][0] * (a[1][1] * a[2][2] - a[1][2] * a[2][1])
                - a[0][1] * (a[1][0] * a[2][2] - a[1][2] * a[2][0])
                + a[0][2] * (a[1][0] * a[2][1] - a[1][1] * a[2][0]))

    base = det3(m)
    if abs(base) < 1e-9:
        raise SystemExit("圆角拟合退化，无法求解。")

    def solve(column: int) -> float:
        replaced = [row[:] for row in m]
        for row in range(3):
            replaced[row][column] = v[row]
        return det3(replaced) / base

    d, e, f = solve(0), solve(1), solve(2)
    center_x, center_y = -d / 2.0, -e / 2.0
    radius = (center_x ** 2 + center_y ** 2 - f) ** 0.5

    if not (side * 0.10 < radius < side * 0.50):
        raise SystemExit(
            f"拟合出的圆角半径 {radius:.1f} 与边长 {side} 不成比例，请检查源图。")

    rms = (sum((((x - center_x) ** 2 + (y - center_y) ** 2) ** 0.5 - radius) ** 2
               for x, y in arc) / len(arc)) ** 0.5
    print(f"    圆角拟合：半径 {radius:.1f}px，残差 RMS {rms:.2f}px（{len(arc)} 个点）")
    return radius


def compose(source: Image.Image) -> Image.Image:
    """裁掉四周留白、补上透明圆角，输出正方形母图（原始分辨率，不做上采样）。"""
    mask = load_mask(source)
    left, top, right, bottom = measure(mask)
    content_width = right - left + 1
    content_height = bottom - top + 1
    print(f"    图案范围：x[{left},{right}] y[{top},{bottom}]  {content_width}x{content_height}")

    side = max(content_width, content_height)
    radius = fit_corner_radius(mask, left, top, side)

    # 以图案中心为心取正方形来裁。长宽常差百分之几（源图并不严格正方形），
    # 直接拉伸会把圆角压成椭圆，所以宁可让短边一侧留一条透明边。
    center_x = (left + right + 1) / 2
    center_y = (top + bottom + 1) / 2
    crop = (center_x - side / 2, center_y - side / 2,
            center_x + side / 2, center_y + side / 2)
    artwork = source.crop(tuple(int(round(v)) for v in crop))

    # 母图边长。取原生尺寸，只在源图过大时压到上限——绝不上采样，
    # 否则等于先插值放大再逐档缩小，白丢清晰度。
    master = int(min(MASTER_LIMIT, max(256, side)))
    if master != side:
        artwork = artwork.resize((master, master), Image.LANCZOS)

    scale = master / side
    drawn_width = content_width * scale
    drawn_height = content_height * scale
    offset_x = (master - drawn_width) / 2
    offset_y = (master - drawn_height) / 2

    # 圆角蒙版。半径按同一比例缩放，圆角在图标里仍是正圆。
    # 这一步同时抹掉了源图边缘的抗锯齿与 JPEG 振铃——那些像素会被判为全透明。
    alpha = Image.new("L", (master, master), 0)
    ImageDraw.Draw(alpha).rounded_rectangle(
        (offset_x, offset_y, offset_x + drawn_width - 1, offset_y + drawn_height - 1),
        radius=radius * scale,
        fill=255,
    )

    icon = Image.new("RGBA", (master, master), (0, 0, 0, 0))
    icon.paste(artwork, (0, 0), alpha)
    print(f"    母图 {master}x{master}")
    return icon


def find_source() -> Path:
    """按候选顺序找到品牌源图。"""
    for candidate in SOURCE_CANDIDATES:
        if candidate.exists():
            return candidate
    listing = "\n".join(f"    - {p}" for p in SOURCE_CANDIDATES)
    raise SystemExit(f"找不到品牌源图，以下位置都没有：\n{listing}")


def main() -> int:
    source_path = find_source()
    source = Image.open(source_path).convert("RGB")
    print(f"==> 源图 {source_path.name}  {source.width}x{source.height}  （{source_path.parent}）")

    print("==> 量取图案与圆角")
    icon = compose(source)

    TARGET.parent.mkdir(parents=True, exist_ok=True)
    # 交给 Pillow 落 ico：它会为每档尺寸分别用 LANCZOS 缩一次（因此我们只准备一张
    # 原始分辨率的母图即可），256 档写成 PNG 压缩条目，更小的档写未压缩位图，
    # 这正是 Windows 期望的布局。
    icon.save(TARGET, format="ICO", sizes=SIZES)

    # 标题栏用的位图。带上 ico 里没有的中间档也无所谓——它只服务界面。
    # 同样从母图缩，与 ico 出自同一张源，形状必然一致。
    icon.resize((PNG_SIZE, PNG_SIZE), Image.LANCZOS).save(PNG_TARGET, format="PNG")

    # 回读确认每档都真的写进去了，而不是「看起来成功」。
    with Image.open(TARGET) as check:
        entries = sorted(check.info.get("sizes", []))
    expected = sorted((min(a, b), min(a, b)) for a, b in SIZES)
    missing = [s for s in expected if s not in entries]

    print(f"==> 已生成 {TARGET}")
    print(f"    条目 {len(entries)} 档：{', '.join(str(s[0]) for s in entries)}")
    print(f"    体积 {TARGET.stat().st_size / 1024:.1f} KB")

    with Image.open(PNG_TARGET) as check_png:
        png_size = check_png.size
        png_alpha = check_png.convert("RGBA").getchannel("A")
    print(f"==> 已生成 {PNG_TARGET}")
    print(f"    尺寸 {png_size[0]}x{png_size[1]}，四角 alpha "
          f"{png_alpha.getpixel((0, 0))}/{png_alpha.getpixel((png_size[0] - 1, 0))}/"
          f"{png_alpha.getpixel((0, png_size[1] - 1))}/"
          f"{png_alpha.getpixel((png_size[0] - 1, png_size[1] - 1))}")

    if missing:
        print(f"!! 缺少档位：{missing}")
        return 1
    if png_size != (PNG_SIZE, PNG_SIZE):
        print(f"!! 标题栏位图尺寸不对：{png_size}")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
