// BrightnessShader.fx
sampler2D Input : register(s0);         // The video frame texture
float BrightnessFactor : register(c0);  // Custom brightness factor (1.0 = normal)

float4 main(float2 uv : TEXCOORD) : COLOR
{
    // Sample the color from the input texture (video frame)
    float4 color = tex2D(Input, uv);

    // Apply the brightness factor to the RGB components
    //color.rgb *= BrightnessFactor;
    color.rgb += BrightnessFactor;

    // Return the modified color
    // WPF handles clamping to the [0,1] range automatically for display.
    return color;
}