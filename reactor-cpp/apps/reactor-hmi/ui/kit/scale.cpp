#include "scale.hpp"

#include "icons_gen.hpp"
#include "text_gen.hpp"

namespace reactor::hmi::scale {

float g = 1.0f;

void set(float s) {
    g = s > 0 ? s : 1.0f;
    icons::use_scale(g);
    icons::use_text_scale(g);
}

}  // namespace reactor::hmi::scale
