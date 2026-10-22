// Greybox world material: diffuse lighting with per-vertex colour (box kind tint) and shadows.
// Built-in render pipeline surface shader; replaced by the art pipeline's materials in production.
Shader "RWBR/VertexColorLit"
{
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Lambert fullforwardshadows addshadow
        #pragma target 3.0

        fixed4 _Tint;

        struct Input
        {
            float4 color : COLOR;
        };

        void surf (Input IN, inout SurfaceOutput o)
        {
            o.Albedo = IN.color.rgb * _Tint.rgb;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
