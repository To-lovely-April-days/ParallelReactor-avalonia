# 没有屏幕 / 没装 SDL 的电脑上预览界面：不开窗口，渲染一帧写成 BMP。
# 只用系统自带的 cc / c++ / make，不需要 CMake。字体用 LVGL 自带的 tiny_ttf（TrueType 字体文件）。
#   make -C tools/preview -j8
#   tools/preview/out/reactor-hmi-shot --sim --page main-f1 --shot shot.bmp   # 截一页（--help 列出页面名；--sim = 模拟数据）
#   tools/preview/out/reactor-hmi-shot --sim --shots DIR                      # 所有页面各截一张
#   tools/preview/out/reactor-hmi-shot --server unix:/run/reactord/hmi.sock --page main-f1 --shot x.bmp  # 连真后台截图
#   tools/preview/out/reactor-hmi-shot --selftest-net                          # 网络层自检（内置假服务器）
#   make -C tools/preview shots                                            # 同上，写到 out/shots/（macOS 另存一份 PNG）
# apps/reactor-hmi 下的 .cpp 全部自动收集（和 CMake 的 glob 一致），加文件不用改这里。
ROOT := ../..
OUT := out
LVGL := $(ROOT)/third_party/lvgl
APP := $(ROOT)/apps/reactor-hmi
DEFS := -DLV_CONF_INCLUDE_SIMPLE -DLV_LVGL_H_INCLUDE_SIMPLE -DREACTOR_USE_TINY_TTF=1 -DREACTOR_HEADLESS=1
INC := -I$(ROOT)/config -I$(LVGL) -I$(APP) -I$(ROOT)/third_party
CFLAGS := -O2 -w $(DEFS) $(INC)
CXXFLAGS := -O2 -std=c++17 -Wall -Wextra -Wno-unused-parameter -Wno-missing-field-initializers $(DEFS) $(INC)

LV_SRCS := $(shell find $(LVGL)/src -name '*.c' -not -path '*/thorvg/*')
LV_OBJS := $(patsubst $(LVGL)/%.c,$(OUT)/lvgl/%.o,$(LV_SRCS))
APP_SRCS := $(shell find $(APP) -name '*.cpp')
APP_HDRS := $(shell find $(APP) -name '*.hpp')
APP_OBJS := $(patsubst $(APP)/%.cpp,$(OUT)/app/%.o,$(APP_SRCS))

all: $(OUT)/reactor-hmi-shot

$(OUT)/lvgl/%.o: $(LVGL)/%.c $(ROOT)/config/lv_conf.h
	@mkdir -p $(dir $@)
	@$(CC) $(CFLAGS) -c $< -o $@

# 图标数组很大（几 MB 的十六进制），单独一条规则：不优化、不跟着别的头文件重编
$(OUT)/app/ui/kit/icons_gen.o: $(APP)/ui/kit/icons_gen.cpp $(APP)/ui/kit/icons_gen.hpp
	@mkdir -p $(dir $@)
	$(CXX) $(CXXFLAGS) -O0 -c $< -o $@

$(OUT)/app/%.o: $(APP)/%.cpp $(APP_HDRS) $(ROOT)/config/lv_conf.h
	@mkdir -p $(dir $@)
	$(CXX) $(CXXFLAGS) -c $< -o $@

$(OUT)/reactor-hmi-shot: $(LV_OBJS) $(APP_OBJS)
	@echo "  LINK $@" && $(CXX) $(CXXFLAGS) $(APP_OBJS) $(LV_OBJS) -o $@

shots: $(OUT)/reactor-hmi-shot
	@mkdir -p $(OUT)/shots
	$(OUT)/reactor-hmi-shot --sim --shots $(OUT)/shots
	@if command -v sips >/dev/null; then for f in $(OUT)/shots/*.bmp; do sips -s format png $$f --out $${f%.bmp}.png >/dev/null; done; fi

clean:
	rm -rf $(OUT)
selftest-net: $(OUT)/reactor-hmi-shot
	$(OUT)/reactor-hmi-shot --selftest-net

.PHONY: all clean shots selftest-net
