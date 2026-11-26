// 生成文件，不要手改：python3 assets/schema_sample.py
// 内容 = src/core/program/program_schema.cpp 按出厂设置生成的 GET /program-schema 正文（模拟数据源用）。
namespace reactor::hmi::model {
extern const char* const kSampleProgramSchema;
const char* const kSampleProgramSchema = R"SCHEMA({
 "apiVersion": "1.0",
 "settingsRevision": 1,
 "stepTypes": [
  "leak",
  "exchange",
  "stir",
  "heat",
  "press",
  "hold",
  "react",
  "vent",
  "end"
 ],
 "steps": [
  {
   "type": "leak",
   "label": "气密检查",
   "chip": "LEAK",
   "help": "用惰性气体把这一路充到试验压力，关阀静置后保压计时，按温度补偿后的压降速率判定。保压期间不占用气路，别的路可以同时充气。试验压力建议不低于后面反应的压力；「允许压降」默认取设置里的泄漏率阈值。",
   "fields": [
    {
     "key": "pres",
     "label": "试验压力",
     "kind": "num",
     "unit": "psi",
     "default": 435.1,
     "min": 14.5,
     "max": 339.29,
     "declaredMin": 14.5,
     "declaredMax": 493.1,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    },
    {
     "key": "settle",
     "label": "稳定时间",
     "kind": "num",
     "unit": "分钟",
     "default": 5,
     "min": 0,
     "max": 60,
     "declaredMin": 0,
     "declaredMax": 60,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    },
    {
     "key": "hold",
     "label": "保压时间",
     "kind": "num",
     "unit": "分钟",
     "default": 30,
     "min": 5,
     "max": 600,
     "declaredMin": 5,
     "declaredMax": 600,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    },
    {
     "key": "maxDrop",
     "label": "允许压降",
     "kind": "num",
     "unit": "psi/h",
     "default": 2,
     "min": 0.1,
     "max": 100,
     "declaredMin": 0.1,
     "declaredMax": 100,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    },
    {
     "key": "onFail",
     "label": "不通过时",
     "kind": "sel",
     "unit": null,
     "default": null,
     "min": null,
     "max": null,
     "declaredMin": null,
     "declaredMax": null,
     "options": [
      {
       "code": "stop",
       "label": "停止这一路",
       "available": true,
       "reason": null
      },
      {
       "code": "continue",
       "label": "记录后继续",
       "available": true,
       "reason": null
      }
     ],
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": "stop"
    },
    {
     "key": "after",
     "label": "之后",
     "kind": "sel",
     "unit": null,
     "default": null,
     "min": null,
     "max": null,
     "declaredMin": null,
     "declaredMax": null,
     "options": [
      {
       "code": "vent",
       "label": "放空",
       "available": true,
       "reason": null
      },
      {
       "code": "keep",
       "label": "保持压力",
       "available": true,
       "reason": null
      }
     ],
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": "vent"
    }
   ]
  },
  {
   "type": "exchange",
   "label": "置换",
   "chip": "EXCHANGE",
   "help": "把釜充到「充到」再放到「放到」以下，重复「次数」次，真正换掉釜里的气；可先吹扫总管（SV 关着，把上一种气赶出总管）。单次充或放超过「单次最长」就报警停止这一路（充不上去：检查气源与减压阀；放不下来：可能有进气单向阀，不要开釜）。次数 0 = 只吹总管。",
   "fields": [
    {
     "key": "gas",
     "label": "气体",
     "kind": "sel",
     "unit": null,
     "default": null,
     "min": null,
     "max": null,
     "declaredMin": null,
     "declaredMax": null,
     "options": [
      {
       "code": "inert",
       "label": "惰性气体",
       "available": true,
       "reason": null
      },
      {
       "code": "A",
       "label": "气体 A",
       "available": true,
       "reason": null
      },
      {
       "code": "B",
       "label": "气体 B",
       "available": true,
       "reason": null
      }
     ],
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": "inert"
    },
    {
     "key": "fill",
     "label": "充到",
     "kind": "num",
     "unit": "psi",
     "default": 72.5,
     "min": 7.3,
     "max": 339.29,
     "declaredMin": 7.3,
     "declaredMax": 493.1,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    },
    {
     "key": "release",
     "label": "放到",
     "kind": "num",
     "unit": "psi",
     "default": 2.9,
     "min": 0,
     "max": 493.1,
     "declaredMin": 0,
     "declaredMax": 493.1,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    },
    {
     "key": "cycles",
     "label": "次数",
     "kind": "num",
     "unit": "次",
     "default": 3,
     "min": 0,
     "max": 20,
     "declaredMin": 0,
     "declaredMax": 20,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    },
    {
     "key": "lineFlush",
     "label": "先吹扫总管",
     "kind": "num",
     "unit": "次",
     "default": 2,
     "min": 0,
     "max": 10,
     "declaredMin": 0,
     "declaredMax": 10,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    },
    {
     "key": "maxSec",
     "label": "单次最长",
     "kind": "num",
     "unit": "秒",
     "default": 60,
     "min": 10,
     "max": 300,
     "declaredMin": 10,
     "declaredMax": 300,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    }
   ]
  },
  {
   "type": "stir",
   "label": "搅拌",
   "chip": "STIR",
   "help": "设定釜里的转速（电机 = 釜 × 传动比），立即进入下一步，转速一直保持到下一个「搅拌」或程序结束；0 = 停。一台电机带全部釜：同时运行的几路程序转速应一致，改写别的程序正在用的转速会记一条提醒。要「搅 10 分钟」用「搅拌 → 保持 10 min」。",
   "fields": [
    {
     "key": "rpm",
     "label": "转速",
     "kind": "num",
     "unit": "rpm",
     "default": 400,
     "min": 0,
     "max": 450,
     "declaredMin": 0,
     "declaredMax": 3000,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    }
   ]
  },
  {
   "type": "heat",
   "label": "升温",
   "chip": "HEAT",
   "help": "定值：升到目标温度，实测进入「到位范围」后再平衡「到位后平衡」分钟（让溶液温度均匀）；升温速率 0 = 尽快（防过冲逼近）。曲线：跑完各段后同样等实测温度到位（最后一段的温度）。超过「最长等待」仍未到位按「超时」处理。加热一直保持到下一个升温或程序结束。",
   "fields": [
    {
     "key": "mode",
     "label": "升温方式",
     "kind": "sel",
     "unit": null,
     "default": null,
     "min": null,
     "max": null,
     "declaredMin": null,
     "declaredMax": null,
     "options": [
      {
       "code": "fixed",
       "label": "定值",
       "available": true,
       "reason": null
      },
      {
       "code": "curve",
       "label": "曲线",
       "available": true,
       "reason": null
      }
     ],
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": "fixed"
    },
    {
     "key": "temp",
     "label": "目标温度",
     "kind": "num",
     "unit": "°C",
     "default": 80,
     "min": 35,
     "max": 199,
     "declaredMin": 35,
     "declaredMax": 300,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": {
      "key": "mode",
      "in": [
       "fixed"
      ]
     },
     "defaultCode": null
    },
    {
     "key": "rate",
     "label": "升温速率",
     "kind": "num",
     "unit": "°C/min",
     "default": 0,
     "min": 0,
     "max": 20,
     "declaredMin": 0,
     "declaredMax": 20,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": {
      "key": "mode",
      "in": [
       "fixed"
      ]
     },
     "defaultCode": null
    },
    {
     "key": "band",
     "label": "到位范围 ±",
     "kind": "num",
     "unit": "°C",
     "default": 2,
     "min": 0.5,
     "max": 10,
     "declaredMin": 0.5,
     "declaredMax": 10,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    },
    {
     "key": "wait",
     "label": "到位后平衡",
     "kind": "num",
     "unit": "分钟",
     "default": 15,
     "min": 0,
     "max": 120,
     "declaredMin": 0,
     "declaredMax": 120,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": {
      "key": "mode",
      "in": [
       "fixed"
      ]
     },
     "defaultCode": null
    },
    {
     "key": "maxWait",
     "label": "最长等待",
     "kind": "num",
     "unit": "分钟",
     "default": 120,
     "min": 10,
     "max": 600,
     "declaredMin": 10,
     "declaredMax": 600,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    },
    {
     "key": "onTimeout",
     "label": "超时",
     "kind": "sel",
     "unit": null,
     "default": null,
     "min": null,
     "max": null,
     "declaredMin": null,
     "declaredMax": null,
     "options": [
      {
       "code": "stop",
       "label": "停止这一路",
       "available": true,
       "reason": null
      },
      {
       "code": "continue",
       "label": "继续",
       "available": true,
       "reason": null
      }
     ],
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": "stop"
    },
    {
     "key": "segments",
     "label": "曲线段（mode=curve 时使用）",
     "kind": "array",
     "unit": null,
     "default": null,
     "min": null,
     "max": null,
     "declaredMin": null,
     "declaredMax": null,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": [
      {
       "key": "temp",
       "label": "段目标温度",
       "kind": "num",
       "unit": "°C",
       "default": null,
       "min": 0,
       "max": 199,
       "declaredMin": 0,
       "declaredMax": 300,
       "options": null,
       "available": true,
       "reason": null,
       "itemFields": null,
       "minItems": null,
       "maxItems": null,
       "visibleWhen": null,
       "defaultCode": null
      },
      {
       "key": "rate",
       "label": "升温速率",
       "kind": "num",
       "unit": "°C/min",
       "default": null,
       "min": 0,
       "max": 50,
       "declaredMin": 0,
       "declaredMax": 50,
       "options": null,
       "available": true,
       "reason": null,
       "itemFields": null,
       "minItems": null,
       "maxItems": null,
       "visibleWhen": null,
       "defaultCode": null
      },
      {
       "key": "hold",
       "label": "保温时间",
       "kind": "num",
       "unit": "分钟",
       "default": null,
       "min": 0,
       "max": 600,
       "declaredMin": 0,
       "declaredMax": 600,
       "options": null,
       "available": true,
       "reason": null,
       "itemFields": null,
       "minItems": null,
       "maxItems": null,
       "visibleWhen": null,
       "defaultCode": null
      }
     ],
     "minItems": 1,
     "maxItems": 16,
     "visibleWhen": {
      "key": "mode",
      "in": [
       "curve"
      ]
     },
     "defaultCode": null
    }
   ]
  },
  {
   "type": "press",
   "label": "充压",
   "chip": "PRESS",
   "help": "用所选气体充到目标压力（从下方逼近，到「允许偏差」以内算到位）；只能升、不能降，已经高于目标时不动并写明。稳压开 = 压力低于目标 ×（1 − 补气阈值）时自动补气，一直到下一个充压 / 置换 / 放空 / 气密检查或结束；补进去的气不算吸气量。升温会让压力上去，建议先升温到位再充压。",
   "fields": [
    {
     "key": "gas",
     "label": "气体",
     "kind": "sel",
     "unit": null,
     "default": null,
     "min": null,
     "max": null,
     "declaredMin": null,
     "declaredMax": null,
     "options": [
      {
       "code": "inert",
       "label": "惰性气体",
       "available": true,
       "reason": null
      },
      {
       "code": "A",
       "label": "气体 A",
       "available": true,
       "reason": null
      },
      {
       "code": "B",
       "label": "气体 B",
       "available": true,
       "reason": null
      }
     ],
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": "A"
    },
    {
     "key": "pres",
     "label": "目标压力",
     "kind": "num",
     "unit": "psi",
     "default": 290.1,
     "min": 7.3,
     "max": 339.29,
     "declaredMin": 7.3,
     "declaredMax": 493.1,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    },
    {
     "key": "tol",
     "label": "允许偏差",
     "kind": "num",
     "unit": "%",
     "default": 2,
     "min": 0.5,
     "max": 10,
     "declaredMin": 0.5,
     "declaredMax": 10,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    },
    {
     "key": "regulate",
     "label": "之后稳压",
     "kind": "sel",
     "unit": null,
     "default": null,
     "min": null,
     "max": null,
     "declaredMin": null,
     "declaredMax": null,
     "options": [
      {
       "code": "on",
       "label": "开启",
       "available": true,
       "reason": null
      },
      {
       "code": "off",
       "label": "关闭",
       "available": true,
       "reason": null
      }
     ],
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": "on"
    },
    {
     "key": "refill",
     "label": "补气阈值",
     "kind": "num",
     "unit": "%",
     "default": 3,
     "min": 1,
     "max": 10,
     "declaredMin": 1,
     "declaredMax": 10,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": {
      "key": "regulate",
      "in": [
       "on"
      ]
     },
     "defaultCode": null
    },
    {
     "key": "maxSec",
     "label": "最长充气",
     "kind": "num",
     "unit": "秒",
     "default": 180,
     "min": 30,
     "max": 600,
     "declaredMin": 30,
     "declaredMax": 600,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    }
   ]
  },
  {
   "type": "hold",
   "label": "保持",
   "chip": "HOLD",
   "help": "什么都不动，等条件：时间到，或温度 / 压力达到步骤里写的目标值（每 1 s 判断一次）。按温度 / 压力等待时，超过「最长等待」或传感器离线超过 60 s 按「超时」处理。",
   "fields": [
    {
     "key": "until",
     "label": "结束条件",
     "kind": "sel",
     "unit": null,
     "default": null,
     "min": null,
     "max": null,
     "declaredMin": null,
     "declaredMax": null,
     "options": [
      {
       "code": "time",
       "label": "时间到",
       "available": true,
       "reason": null
      },
      {
       "code": "tempAbove",
       "label": "温度 ≥",
       "available": true,
       "reason": null
      },
      {
       "code": "tempBelow",
       "label": "温度 ≤",
       "available": true,
       "reason": null
      },
      {
       "code": "pressAbove",
       "label": "压力 ≥",
       "available": true,
       "reason": null
      },
      {
       "code": "pressBelow",
       "label": "压力 ≤",
       "available": true,
       "reason": null
      }
     ],
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": "time"
    },
    {
     "key": "time",
     "label": "时间",
     "kind": "num",
     "unit": "分钟",
     "default": 10,
     "min": 0,
     "max": 1440,
     "declaredMin": 0,
     "declaredMax": 1440,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": {
      "key": "until",
      "in": [
       "time"
      ]
     },
     "defaultCode": null
    },
    {
     "key": "temp",
     "label": "目标温度",
     "kind": "num",
     "unit": "°C",
     "default": 40,
     "min": 0,
     "max": 199,
     "declaredMin": 0,
     "declaredMax": 300,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": {
      "key": "until",
      "in": [
       "tempAbove",
       "tempBelow"
      ]
     },
     "defaultCode": null
    },
    {
     "key": "pres",
     "label": "目标压力",
     "kind": "num",
     "unit": "psi",
     "default": 14.5,
     "min": 0,
     "max": 339.29,
     "declaredMin": 0,
     "declaredMax": 493.1,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": {
      "key": "until",
      "in": [
       "pressAbove",
       "pressBelow"
      ]
     },
     "defaultCode": null
    },
    {
     "key": "maxWait",
     "label": "最长等待",
     "kind": "num",
     "unit": "分钟",
     "default": 60,
     "min": 1,
     "max": 1440,
     "declaredMin": 1,
     "declaredMax": 1440,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": {
      "key": "until",
      "in": [
       "tempAbove",
       "tempBelow",
       "pressAbove",
       "pressBelow"
      ]
     },
     "defaultCode": null
    },
    {
     "key": "onTimeout",
     "label": "超时",
     "kind": "sel",
     "unit": null,
     "default": null,
     "min": null,
     "max": null,
     "declaredMin": null,
     "declaredMax": null,
     "options": [
      {
       "code": "continue",
       "label": "继续",
       "available": true,
       "reason": null
      },
      {
       "code": "stop",
       "label": "停止这一路",
       "available": true,
       "reason": null
      }
     ],
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": {
      "key": "until",
      "in": [
       "tempAbove",
       "tempBelow",
       "pressAbove",
       "pressBelow"
      ]
     },
     "defaultCode": "continue"
    }
   ]
  },
  {
   "type": "react",
   "label": "反应",
   "chip": "REACT",
   "help": "不动阀、不改温度：温度靠前面的升温保持，压力靠充压的稳压，搅拌靠前面的搅拌。每 2 s 判断一次，先到者结束：最长时间到；过了最短时间且吸气量达到目标；过了最短时间且吸气速率连续「持续」分钟低于「停止速率」。反应前没有充压时吸气量不计量，只按最长时间结束。",
   "fields": [
    {
     "key": "time",
     "label": "最长时间",
     "kind": "num",
     "unit": "分钟",
     "default": 480,
     "min": 1,
     "max": 4320,
     "declaredMin": 1,
     "declaredMax": 4320,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    },
    {
     "key": "minTime",
     "label": "最短时间",
     "kind": "num",
     "unit": "分钟",
     "default": 30,
     "min": 0,
     "max": 4320,
     "declaredMin": 0,
     "declaredMax": 4320,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    },
    {
     "key": "mmolOn",
     "label": "吸气量达到",
     "kind": "sel",
     "unit": null,
     "default": null,
     "min": null,
     "max": null,
     "declaredMin": null,
     "declaredMax": null,
     "options": [
      {
       "code": "on",
       "label": "开启",
       "available": true,
       "reason": null
      },
      {
       "code": "off",
       "label": "关闭",
       "available": true,
       "reason": null
      }
     ],
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": "off"
    },
    {
     "key": "mmol",
     "label": "目标吸气量",
     "kind": "num",
     "unit": "mmol",
     "default": 5,
     "min": 0.01,
     "max": 100,
     "declaredMin": 0.01,
     "declaredMax": 100,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": {
      "key": "mmolOn",
      "in": [
       "on"
      ]
     },
     "defaultCode": null
    },
    {
     "key": "stallOn",
     "label": "吸气停止",
     "kind": "sel",
     "unit": null,
     "default": null,
     "min": null,
     "max": null,
     "declaredMin": null,
     "declaredMax": null,
     "options": [
      {
       "code": "on",
       "label": "开启",
       "available": true,
       "reason": null
      },
      {
       "code": "off",
       "label": "关闭",
       "available": true,
       "reason": null
      }
     ],
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": "on"
    },
    {
     "key": "stallRate",
     "label": "停止速率",
     "kind": "num",
     "unit": "mmol/min",
     "default": 0.005,
     "min": 0.001,
     "max": 1,
     "declaredMin": 0.001,
     "declaredMax": 1,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": {
      "key": "stallOn",
      "in": [
       "on"
      ]
     },
     "defaultCode": null
    },
    {
     "key": "stallMin",
     "label": "持续",
     "kind": "num",
     "unit": "分钟",
     "default": 30,
     "min": 5,
     "max": 240,
     "declaredMin": 5,
     "declaredMax": 240,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": {
      "key": "stallOn",
      "in": [
       "on"
      ]
     },
     "defaultCode": null
    }
   ]
  },
  {
   "type": "vent",
   "label": "放空",
   "chip": "VENT",
   "help": "开 SVn + 排空阀，读到釜压 ≤「放到」才算放完；超过「最长时间」还放不下来就报警停止这一路（釜里的气放不掉，不要开釜）。之后可用惰性气体置换几次，把残余的反应气赶走再开釜。釜温高于 60 °C 时放空会带出溶剂蒸气（不拦，可在前面加「保持 · 温度 ≤ x」）。",
   "fields": [
    {
     "key": "to",
     "label": "放到",
     "kind": "num",
     "unit": "psi",
     "default": 2.9,
     "min": 0,
     "max": 72.5,
     "declaredMin": 0,
     "declaredMax": 72.5,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    },
    {
     "key": "maxSec",
     "label": "最长时间",
     "kind": "num",
     "unit": "秒",
     "default": 120,
     "min": 10,
     "max": 600,
     "declaredMin": 10,
     "declaredMax": 600,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    },
    {
     "key": "flush",
     "label": "之后惰性置换",
     "kind": "num",
     "unit": "次",
     "default": 3,
     "min": 0,
     "max": 10,
     "declaredMin": 0,
     "declaredMax": 10,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    },
    {
     "key": "flushPres",
     "label": "置换充到",
     "kind": "num",
     "unit": "psi",
     "default": 43.5,
     "min": 7.3,
     "max": 339.29,
     "declaredMin": 7.3,
     "declaredMax": 493.1,
     "options": null,
     "available": true,
     "reason": null,
     "itemFields": null,
     "minItems": null,
     "maxItems": null,
     "visibleWhen": null,
     "defaultCode": null
    }
   ]
  },
  {
   "type": "end",
   "label": "结束",
   "chip": "END",
   "help": "固定的最后一步（不能删、不能挪）：关加热、所有阀关、释放搅拌（别的路还在用就继续转）、停止吸气计量、写结束原因。不降温：结束时釜可能还是热的；需要等凉了再放空，可在放空前加「保持 · 温度 ≤ x」。",
   "fields": []
  }
 ]
})SCHEMA";
}  // namespace reactor::hmi::model
