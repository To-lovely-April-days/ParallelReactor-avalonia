"""Regenerate ../model/sim_schema_gen.cpp — the sample GET /program-schema body that SimSource serves.

The JSON is produced by the backend's own code (src/core/program/program_schema.cpp) so the touch UI's
parameter panels are driven by exactly what reactord will return. The HMI does not link that code; this
script compiles a tiny throw-away program in a temp dir, runs it and embeds the output as a raw string.

  python3 schema_sample.py        (needs a C++17 compiler: c++ / g++)

Limits used = settings defaults (tempSpMax 200 / overTemp 200 / rpmMax 450 / pressureSpMax 507.6 psi /
overPressure 377.1 psi), leak rate 2 psi/h, settingsRevision 1.
"""
import os, subprocess, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..', '..'))
SRC = r'''
#include "core/program/program_schema.hpp"
#include <iostream>
using namespace reactor::core::program;
int main() {
    ProgramLimitSettings l; l.t_max_sp = 200; l.over_temp = 200; l.rpm_max = 450; l.p_max_sp = 507.6; l.over_pressure = 377.1;
    std::cout << program_schema::build(l, 1, 2).dump(1) << std::endl;
}
'''

def main():
    tmp = tempfile.mkdtemp()
    cpp = os.path.join(tmp, 'gen.cpp'); exe = os.path.join(tmp, 'gen')
    open(cpp, 'w').write(SRC)
    srcs = [os.path.join(d, f) for sub in ('src/core/program', 'src/core', 'src/common')
            for d in [os.path.join(ROOT, sub)] for f in sorted(os.listdir(d)) if f.endswith('.cpp')]
    cxx = os.environ.get('CXX', 'c++')
    subprocess.check_call([cxx, '-std=c++17', '-O0', '-w', '-I' + os.path.join(ROOT, 'src'),
                           '-I' + os.path.join(ROOT, 'third_party'), cpp] + srcs + ['-o', exe])
    out = subprocess.check_output([exe]).decode('utf8').strip()
    dst = os.path.join(HERE, '..', 'model', 'sim_schema_gen.cpp')
    with open(dst, 'w', encoding='utf8') as f:
        f.write('// 生成文件，不要手改：python3 assets/schema_sample.py\n')
        f.write('// 内容 = src/core/program/program_schema.cpp 按出厂设置生成的 GET /program-schema 正文（模拟数据源用）。\n')
        f.write('namespace reactor::hmi::model {\nextern const char* const kSampleProgramSchema;\n')
        f.write('const char* const kSampleProgramSchema = R"SCHEMA(' + out + ')SCHEMA";\n}  // namespace reactor::hmi::model\n')
    print('wrote', os.path.normpath(dst), len(out), 'bytes')

if __name__ == '__main__':
    main()
