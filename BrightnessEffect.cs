using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Effects;
using System.Windows.Media;
using System.Windows;

using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

// Make sure this namespace matches your project's namespace or is appropriately referenced
namespace SubtitleVideoPlayerWpf // Or your specific project namespace
{
    public class BrightnessEffect : ShaderEffect
    {
        private static PixelShader _pixelShader = new PixelShader();

        static BrightnessEffect()
        {
            // Replace "YourAssemblyName" with the actual assembly name of your project.
            // You can find this in Project Properties -> Application -> Assembly name.
            // If your Shaders folder is at the root of the project:
            _pixelShader.UriSource = new Uri("pack://application:,,,/SubtitleVideoPlayerWpf;component/Resources/BrightnessShader.ps");
        }

        public BrightnessEffect()
        {
            this.PixelShader = _pixelShader;

            // This explicitly updates the shader with the initial values of the properties.
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(BrightnessFactorProperty);
        }

        // DependencyProperty for the implicit input (the visual to which the effect is applied)
        public static readonly DependencyProperty InputProperty =
            ShaderEffect.RegisterPixelShaderSamplerProperty("Input", typeof(BrightnessEffect), 0);

        // The "Input" here matches "sampler2D Input" in the HLSL code.
        // This property will be automatically set by WPF to the visual of the MediaElement.
        public Brush Input
        {
            get { return (Brush)GetValue(InputProperty); }
            set { SetValue(InputProperty, value); }
        }

        // DependencyProperty for the BrightnessFactor
        // This maps to the "float BrightnessFactor : register(c0);" in the HLSL code.
        public static readonly DependencyProperty BrightnessFactorProperty =
            DependencyProperty.Register("BrightnessFactor", typeof(double), typeof(BrightnessEffect),
                new UIPropertyMetadata(
                    0.1, // Default value (1.0 = normal brightness)
                    PixelShaderConstantCallback(0) // Map to constant register c0
                ));

        public double BrightnessFactor
        {
            get { return (double)GetValue(BrightnessFactorProperty); }
            set { SetValue(BrightnessFactorProperty, value); }
        }
    }
}