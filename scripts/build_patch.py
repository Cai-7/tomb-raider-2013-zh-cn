# -*- coding: utf-8 -*-
r"""把《古墓丽影9》官方繁体中文文本转换为简体中文。

输入是 TrTextTool 提取出的 JSON：<game>\zhhant\pc-w\local\locals.json
输出是可直接作为 mod 安装的 JSON：<mod>\pc-w\local\locals.bin\zhhant.json

用法：
    python build_patch.py --input work\zhhant\pc-w\local\locals.json \
                          --output mod\pc-w\local\locals.bin\zhhant.json
"""
import argparse
import json
import os
import sys

try:
    from opencc import OpenCC
except ImportError:
    sys.exit("缺少依赖，请先执行：pip install opencc-python-reimplemented")

# 台湾用语 -> 大陆用语。在繁转简之前替换，避免 OpenCC 转出台湾习惯译法。
VOCAB = {
    "蘿拉": "劳拉", "蘿菈": "劳拉", "妳": "你",
    "滑鼠": "鼠标", "遊標": "光标", "游標": "光标",
    "螢幕": "屏幕", "螢屏": "屏幕", "寬螢幕": "宽屏",
    "網際網路": "互联网", "網路": "网络",
    "軟體": "软件", "硬體": "硬件", "光碟": "光盘", "燒錄": "刻录",
    "記憶體": "内存", "快取": "缓存",
    "預設": "默认", "伺服器": "服务器", "資訊": "信息",
    "快速鍵": "快捷键", "捷徑": "快捷方式",
    "驅動程式": "驱动程序", "應用程式": "应用程序", "安裝程式": "安装程序",
    "疑難排解": "疑难解答",
    "載入": "加载", "儲存": "保存",
    "支援": "支持", "匯出": "导出", "匯入": "导入",
    "視訊": "视频", "影片": "视频",
    "解析度": "分辨率", "影格": "帧", "貼圖": "贴图", "材質": "材质",
    "著色器": "着色器", "著色": "着色",
    "品質": "质量", "畫質": "画质",
    "搖桿": "手柄", "控制器": "手柄",
    "行動裝置": "移动设备", "光碟機": "光驱",
    "當機": "死机", "重灌": "重装",
    "點選": "点击",
    "音效卡": "声卡", "顯示卡": "显卡", "顯卡": "显卡",
    "使用者": "用户", "帳戶": "账户", "帳號": "账号",
    "頻寬": "带宽",
    "字型": "字体", "字碼": "编码",
    "程式": "程序", "檔案": "文件", "資料夾": "文件夹", "目錄": "目录",
    "遠端": "远程", "遊戲階段": "游戏会话", "連線": "联机",
}

# 台湾句式 -> 大陆说法（在繁转简之后处理，此时已是简体字）
PHRASES_AFTER = [
    ("进行 Tomb Raider 游戏期间", "运行 Tomb Raider 时"),
    ("进行 Tomb Raider 游戏", "运行 Tomb Raider"),
    ("进行游戏", "运行游戏"),
    ("执行", "运行"),
]

cc = OpenCC("t2s")


def convert(text: str) -> str:
    for tw, cn in VOCAB.items():
        text = text.replace(tw, cn)
    text = cc.convert(text)
    for old, new in PHRASES_AFTER:
        text = text.replace(old, new)
    return text


def main() -> int:
    parser = argparse.ArgumentParser(description="古墓丽影9 官方繁体 -> 简体转换")
    parser.add_argument("--input", required=True, help="TrTextTool 提取的 zh-Hant JSON")
    parser.add_argument("--output", required=True, help="输出的 mod JSON 路径")
    args = parser.parse_args()

    if not os.path.isfile(args.input):
        sys.exit(f"错误：找不到输入文件 {args.input}")

    try:
        with open(args.input, encoding="utf-8") as f:
            data = json.load(f)
    except json.JSONDecodeError as ex:
        sys.exit(f"错误：输入不是合法的 JSON（{args.input}）：{ex}")

    if not isinstance(data, dict):
        sys.exit(f"错误：输入 JSON 顶层应为对象，实际是 {type(data).__name__}")

    out = {k: convert(v) for k, v in data.items()}

    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    with open(args.output, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)

    # 自检：不允许残留繁体字，也不允许丢失按钮图标（私有区字符）
    residual = [k for k, v in out.items() if cc.convert(v) != v]
    glyphs_in = sum(1 for v in data.values() if any(0xE000 <= ord(c) <= 0xF8FF for c in v))
    glyphs_out = sum(1 for v in out.values() if any(0xE000 <= ord(c) <= 0xF8FF for c in v))

    print(f"文本条数: {len(out)}")
    print(f"繁体残留: {len(residual)}")
    print(f"按钮图标: 源 {glyphs_in} / 输出 {glyphs_out}")

    if residual:
        print("警告：仍有繁体残留，例如：", residual[:5])
        return 1
    if glyphs_in != glyphs_out:
        print("警告：按钮图标数量不一致，可能丢失了 <font> 内的图标字符")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
