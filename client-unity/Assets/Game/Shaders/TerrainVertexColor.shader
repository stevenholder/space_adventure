// Lit terrain that actually reads the mesh's vertex colours.
//
// This exists because Unity's built-in Standard shader ignores mesh.colors
// completely. TerrainMesh writes a height-and-slope tint into every vertex,
// Standard discarded all of it, and the planet rendered pure white with a dark
// unlit side — which reads as "the lighting is broken" rather than "the shader
// never looked at the colours".
//
// Lambert, not Standard: the terrain wants no specular highlight. A rocky
// surface with Standard's default smoothness blows out to white under a
// directional light, which is the other half of what that first screenshot was
// showing.
//
// ShaderLab is a text asset, so it stays reviewable in a diff — unlike a
// .shadergraph, which is JSON with embedded GUIDs and is exactly what
// CONVENTIONS.md keeps out of this repo.

Shader "SpaceAdventure/TerrainVertexColor"
{
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 150

        CGPROGRAM
        #pragma surface surf Lambert vertex:vert
        #pragma target 3.0
        // The rock scatter draws ~400 props through
        // Graphics.RenderMeshInstanced, which refuses any material whose
        // shader has no instancing variant: "Material needs to enable
        // instancing for use with RenderMeshInstanced". The variant costs
        // nothing when nothing instances.
        #pragma multi_compile_instancing

        fixed4 _Tint;

        struct Input
        {
            float4 vertexColor : COLOR;
        };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.vertexColor = v.color;
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            o.Albedo = IN.vertexColor.rgb * _Tint.rgb;
            o.Alpha = 1;
        }
        ENDCG
    }

    Fallback "Diffuse"
}
