// 生成文件（assets/text2a8.py），不要手改。随包字体里没有字形的几段文字，预渲染成 A8 图。
#pragma once
#include "lvgl.h"

namespace reactor::hmi::icons {
extern lv_image_dsc_t txt_korean18;  // 한국어
// 换成这个界面缩放用的图（1 或 1.5）；建页面之前调用
void use_text_scale(float s);
}  // namespace reactor::hmi::icons
