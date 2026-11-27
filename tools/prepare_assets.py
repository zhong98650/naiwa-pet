"""准备奶蛙素材：从原作者的素材包生成程序需要的 PNG 帧。

素材来源：https://github.com/yugh6/NaiJia （角色/奶蛙/素材.nwp、定格.png）
素材许可是 PolyForm Noncommercial 1.0.0，仅限非商业使用，详见 NOTICE.md。
所以这些图片不进本仓库，构建前跑一次本脚本即可。

用法：
    python tools/prepare_assets.py
产物（写入 src/assets/）：
    pet.png            待机站姿
    blink0..6.png      眨眼序列（7 帧）
    laugh/0000..0421.png  大笑动画（422 帧，30fps）
    pet_hand.png       单手前伸姿势（由 定格.png 生成，已对齐到站姿坐标系）
"""
import io
import os
import ssl
import statistics
import sys
import urllib.parse
import urllib.request
import zipfile

from PIL import Image

REPO = "https://raw.githubusercontent.com/yugh6/NaiJia/main/"
PACK = "角色/奶蛙/素材.nwp"
POSE = "角色/奶蛙/定格.png"
UA = {"User-Agent": "naiwa-pet-assets/1.0"}

# 站姿画布 288x368；单手姿势画布要在左边多留 44px 放手臂，所以是 332x368
IDLE_CANVAS = (288, 368)
HAND_LEFT_MARGIN = 44

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "src", "assets")


def fetch(name, timeout=180):
    url = REPO + urllib.parse.quote(name)
    last = None
    for _ in range(3):
        try:
            return urllib.request.urlopen(
                urllib.request.Request(url, headers=UA), timeout=timeout,
                context=ssl.create_default_context()).read()
        except Exception as e:      # 网络抖动就重试
            last = e
    raise SystemExit(f"下载失败 {name}: {last}\n（网络不通的话，可以从上面仓库手动下载后放到 src/assets/）")


def iris_center(im):
    """找眼睛里的绿色瞳孔质心，用来把不同姿势的图对齐。"""
    px = im.load()
    pts = []
    for y in range(0, im.height, 2):
        for x in range(0, im.width, 2):
            r, g, b, a = px[x, y]
            if a > 200 and g > 90 and g > r + 35 and g > b + 25:
                pts.append((x, y))
    if not pts:
        return None
    return (statistics.mean(p[0] for p in pts), statistics.mean(p[1] for p in pts))


def main():
    out = os.path.abspath(OUT)
    os.makedirs(os.path.join(out, "laugh"), exist_ok=True)

    print("下载素材包（约 25MB）…")
    pack = zipfile.ZipFile(io.BytesIO(fetch(PACK)))
    names = set(pack.namelist())

    def read(name):
        with pack.open(name) as f:
            return Image.open(io.BytesIO(f.read())).convert("RGBA")

    idle = read("idle.png")
    idle.save(os.path.join(out, "pet.png"))
    print("  pet.png", idle.size)

    for i in range(7):
        read(f"blink/0/{i:02d}.png").save(os.path.join(out, f"blink{i}.png"))
    print("  blink0..6.png")

    laugh = sorted(n for n in names if n.startswith("laugh/") and n.endswith(".png"))
    for n in laugh:
        read(n).save(os.path.join(out, "laugh", os.path.basename(n)))
    print(f"  laugh/ 共 {len(laugh)} 帧")

    print("下载单手姿势…")
    import tempfile
    with tempfile.NamedTemporaryFile(suffix=".png", delete=False) as tmp:
        tmp.write(fetch(POSE))
        pose_path = tmp.name
    pose = Image.open(pose_path).convert("RGBA")
    os.unlink(pose_path)

    # 按身体高度缩放，再按眼睛位置平移，这样两份姿势在屏幕上的头身位置一致
    ib = idle.getchannel("A").getbbox()
    pb = pose.getchannel("A").getbbox()
    k = (ib[3] - ib[1]) / (pb[3] - pb[1])
    pose = pose.resize((round(pose.width * k), round(pose.height * k)), Image.LANCZOS)

    ci, cp = iris_center(idle), iris_center(pose)
    dx = int(round(ci[0] - cp[0])) if ci and cp else 0
    dy = int(round(ci[1] - cp[1])) if ci and cp else 0

    canvas = Image.new("RGBA", (IDLE_CANVAS[0] + HAND_LEFT_MARGIN, IDLE_CANVAS[1]), (0, 0, 0, 0))
    canvas.paste(pose, (HAND_LEFT_MARGIN + dx, dy), pose)
    canvas.save(os.path.join(out, "pet_hand.png"))
    print(f"  pet_hand.png {canvas.size}（锚点按 (99,73) 用）")

    print("完成 ->", out)


if __name__ == "__main__":
    main()
