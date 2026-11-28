# mbedTLS（第三方，原样拷贝）

- 版本：**3.6.2**（3.6 LTS 分支）
- 来源：官方发布包 https://github.com/Mbed-TLS/mbedtls/releases/download/mbedtls-3.6.2/mbedtls-3.6.2.tar.bz2
  （SHA-256 `8b54fb9bcf4d5a7078028e0520acddefb7900b3e66fec7f7175bb5b7d85ccdca`）
- 许可：Apache-2.0 OR GPL-2.0-or-later（见 LICENSE），本项目按 Apache-2.0 使用
- 只保留编译需要的 `include/`、`library/*.c *.h`、`LICENSE`；源码一字未改。
- 裁剪配置不在这里，在项目自己的 `reactor-cpp/config/mbedtls_config.h`（编译时 `-DMBEDTLS_CONFIG_FILE="mbedtls_config.h"`）。
- 升级：换成新的 3.6.x 发布包里的同名目录，跑一遍单测即可。
