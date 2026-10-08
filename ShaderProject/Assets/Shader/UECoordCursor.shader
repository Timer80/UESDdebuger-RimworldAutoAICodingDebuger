// =============================================================================
// UECoordCursor.shader  ——  UESDdebuger「地图坐标光标」
// -----------------------------------------------------------------------------
// 用途：AI 报出的地图坐标，在地图上画一个炼狱魔王炮（Diabolus Hellsphere
//       Cannon）风格的瞄准光标，让玩家一眼看到坐标到底在哪一格。
//
// 设计原则：**朴素、静态、只为定位**。这是测试模组，不做好看。
//   * 无任何动画：没有雷达扫描、没有声纳脉冲、没有呼吸
//   * 无贴图：全部由 UV 程序化生成，AssetBundle 里只需要这一个 .shader
//   * 元件只有四样：外环 / 四向刻线 / 贯穿十字 / 中心点
//   * 颜色 100% 由 _Color 决定（这就是「可指示颜色」）
//   * 唯一的动态是 _Alpha 淡入淡出 —— 生成/消除时不硬弹，属功能性而非装饰
//
// 注：淡入淡出由 C# 每帧 SetFloat("_Alpha") 驱动，shader 里不读 _Time。
//
// 参数（C# 侧 Material.SetXxx 对应）：
//   _Color      线条颜色 —— 唯一的换色入口
//   _Alpha      整体透明度（淡入淡出）
//   _Intensity  发光强度
//   _RingWidth  线宽（以半径 1 为基准的归一化值）
//
// 与游戏内置 Mote_HellsphereCannon_Target 的区别：
//   内置 shader（Custom/Mote hellsphere target）虽然声明了 _Color，但像素着色器
//   从头到尾没有读取它（输出恒为 ScanTex(极坐标) * ScanMask.r + MainTex），
//   因此那个光标**无法换色**。本 shader 自研，颜色完全可控。
// =============================================================================
Shader "UESDdebuger/CoordCursor"
{
    Properties
    {
        _Color     ("Color",       Color)              = (1, 0.29, 0.12, 1)
        _Alpha     ("Alpha",       Range(0, 2))        = 1
        _Intensity ("Intensity",   Range(0, 4))        = 1.1
        _RingWidth ("Ring Width",  Range(0.002, 0.12)) = 0.022
    }

    SubShader
    {
        Tags
        {
            "Queue"           = "Transparent+151"
            "IgnoreProjector" = "True"
            "RenderType"      = "Transparent"
            "DisableBatching" = "True"
        }

        Pass
        {
            Blend  SrcAlpha One
            ZWrite Off
            ZTest  Always
            Cull   Off

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            fixed4 _Color;
            float  _Alpha;
            float  _Intensity;
            float  _RingWidth;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                return o;
            }

            // 带宽遮罩：dist = 到目标半径/直线的距离，halfWidth = 半宽。
            // fwidth 做屏幕空间抗锯齿，任何缩放倍率下线宽都稳定在 ~1px。
            float BandAA(float dist, float halfWidth)
            {
                float aa = fwidth(dist) * 0.7 + 1e-6;
                return 1.0 - smoothstep(halfWidth - aa, halfWidth + aa, dist);
            }

            float Ring(float r, float radius, float halfWidth)
            {
                return BandAA(abs(r - radius), halfWidth);
            }

            // 轴向线段：dir 为单位方向，线段落在 [r0, r1]，半宽 halfWidth
            float Tick(float2 p, float2 dir, float r0, float r1, float halfWidth)
            {
                float along = dot(p, dir);
                float perp  = abs(dot(p, float2(-dir.y, dir.x)));
                float band  = BandAA(perp, halfWidth);
                float aa    = fwidth(along) * 0.7 + 1e-6;
                float seg   = smoothstep(r0 - aa, r0 + aa, along)
                            * (1.0 - smoothstep(r1 - aa, r1 + aa, along));
                return band * seg;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // 归一化到以中心为原点、半径 1 的圆盘（plane 是正方形，圆外裁掉）
                float2 p = i.uv * 2.0 - 1.0;
                float  r = length(p);

                float aaR  = fwidth(r) * 0.7 + 1e-6;
                float disc = 1.0 - smoothstep(1.0 - aaR, 1.0, r);

                float w = _RingWidth;
                float mask = 0.0;

                // 1) 外环
                mask += Ring(r, 0.955, w);

                // 2) 四向刻线（从外环向内指的短刻度，炼狱魔王炮那圈刻度的观感）
                mask += Tick(p, float2( 1.0,  0.0), 0.800, 0.955, w * 0.9);
                mask += Tick(p, float2(-1.0,  0.0), 0.800, 0.955, w * 0.9);
                mask += Tick(p, float2( 0.0,  1.0), 0.800, 0.955, w * 0.9);
                mask += Tick(p, float2( 0.0, -1.0), 0.800, 0.955, w * 0.9);

                // 3) 贯穿十字（细线，断开正中心，避免糊住目标格）
                //    注意：Tick 的 along 取在 [r0, r1]，所以四个方向必须各写一次 ——
                //    只写 (1,0)/(0,1) 会得到只有右臂和上臂的半截十字。
                mask += Tick(p, float2( 1.0,  0.0), 0.100, 0.955, w * 0.45);
                mask += Tick(p, float2(-1.0,  0.0), 0.100, 0.955, w * 0.45);
                mask += Tick(p, float2( 0.0,  1.0), 0.100, 0.955, w * 0.45);
                mask += Tick(p, float2( 0.0, -1.0), 0.100, 0.955, w * 0.45);

                // 4) 中心点（精确标示目标格中心）
                mask += 1.0 - smoothstep(w * 1.3, w * 2.6, r);

                mask = saturate(mask * disc);

                return fixed4(_Color.rgb * _Intensity, saturate(mask * _Color.a * _Alpha));
            }
            ENDCG
        }
    }

    Fallback Off
}
