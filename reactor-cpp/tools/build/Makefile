# 电脑上（macOS / Linux）不用 CMake 的编译：只要 cc / c++ / make。
# 自动收集 src/**/*.cpp 和 tests/**/*.cpp，新加文件不用改这里。
#   make -C tools/build -j8            编后端库、工具、测试
#   make -C tools/build -j8 test       编完运行全部单测
#   make -C tools/build -j8 reactord   后台服务（模拟硬件可直接跑）
ROOT := ../..
OUT := out
CXX ?= c++
CC ?= cc
# mbedTLS：third_party/mbedtls 原样源码 + 项目自己的裁剪配置 config/reactor_mbedtls_config.h
MBEDTLS_DEFS := -I$(ROOT)/third_party/mbedtls/include -I$(ROOT)/config '-DMBEDTLS_CONFIG_FILE="reactor_mbedtls_config.h"'
CXXFLAGS := -O1 -g -std=c++17 -Wall -Wextra -Wno-unused-parameter -I$(ROOT)/src -I$(ROOT)/third_party -I$(ROOT)/tests $(MBEDTLS_DEFS)
# 第三方源码不开警告（-w），只管能编过
MBEDTLS_CFLAGS := -O2 -g -std=gnu99 -w $(MBEDTLS_DEFS)
LDFLAGS := -lpthread

SRC := $(shell find $(ROOT)/src -name '*.cpp' | sort)
OBJ := $(patsubst $(ROOT)/%.cpp,$(OUT)/%.o,$(SRC))
TEST_SRC := $(shell find $(ROOT)/tests -name '*.cpp' | sort)
TEST_OBJ := $(patsubst $(ROOT)/%.cpp,$(OUT)/%.o,$(TEST_SRC))
MBEDTLS_SRC := $(shell find $(ROOT)/third_party/mbedtls/library -name '*.c' | sort)
MBEDTLS_OBJ := $(patsubst $(ROOT)/%.c,$(OUT)/%.o,$(MBEDTLS_SRC))
DEPS := $(OBJ:.o=.d) $(TEST_OBJ:.o=.d) $(MBEDTLS_OBJ:.o=.d)

all: $(OUT)/run_tests $(OUT)/rtu-probe $(OUT)/reactord

$(OUT)/%.o: $(ROOT)/%.cpp
	@mkdir -p $(dir $@)
	@echo "  CXX $<"
	@$(CXX) $(CXXFLAGS) -MMD -MP -c $< -o $@

$(OUT)/third_party/%.o: $(ROOT)/third_party/%.c
	@mkdir -p $(dir $@)
	@echo "  CC  $<"
	@$(CC) $(MBEDTLS_CFLAGS) -MMD -MP -c $< -o $@

# libreactor.a 里带上 mbedTLS 的目标文件，链接时只要一个库
$(OUT)/libreactor.a: $(OBJ) $(MBEDTLS_OBJ)
	@rm -f $@
	@ar rcs $@ $^

$(OUT)/run_tests: $(TEST_OBJ) $(OUT)/libreactor.a
	$(CXX) $(TEST_OBJ) $(OUT)/libreactor.a $(LDFLAGS) -o $@

$(OUT)/rtu-probe: $(OUT)/apps/rtu-probe/main.o $(OUT)/libreactor.a
	$(CXX) $^ $(LDFLAGS) -o $@

$(OUT)/reactord: $(OUT)/apps/reactord/main.o $(OUT)/libreactor.a
	$(CXX) $^ $(LDFLAGS) -o $@

reactord: $(OUT)/reactord

test: $(OUT)/run_tests
	$(OUT)/run_tests

clean:
	rm -rf $(OUT)

.PHONY: all test clean reactord
-include $(DEPS)
