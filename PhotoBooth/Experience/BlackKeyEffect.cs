using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;

namespace Experience;

public sealed class BlackKeyEffect : ShaderEffect
{
    private static readonly PixelShader Shader = new()
    {
        UriSource = new Uri("pack://application:,,,/Experience;component/Shaders/BlackKey.ps", UriKind.Absolute)
    };
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(BlackKeyEffect), 0);
    public static readonly DependencyProperty ThresholdProperty = DependencyProperty.Register("Threshold", typeof(double),
        typeof(BlackKeyEffect), new UIPropertyMetadata(0.015, PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty SoftnessProperty = DependencyProperty.Register("Softness", typeof(double),
        typeof(BlackKeyEffect), new UIPropertyMetadata(0.03, PixelShaderConstantCallback(1)));
    public static readonly DependencyProperty FadeOpacityProperty = DependencyProperty.Register("FadeOpacity", typeof(double),
        typeof(BlackKeyEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(2)));
    public static readonly DependencyProperty KeyEnabledProperty = DependencyProperty.Register("KeyEnabled", typeof(double),
        typeof(BlackKeyEffect), new UIPropertyMetadata(1.0, PixelShaderConstantCallback(3)));
    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public double Threshold { get => (double)GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    public double Softness { get => (double)GetValue(SoftnessProperty); set => SetValue(SoftnessProperty, value); }
    public double FadeOpacity { get => (double)GetValue(FadeOpacityProperty); set => SetValue(FadeOpacityProperty, value); }
    public double KeyEnabled { get => (double)GetValue(KeyEnabledProperty); set => SetValue(KeyEnabledProperty, value); }
    public BlackKeyEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(ThresholdProperty);
        UpdateShaderValue(SoftnessProperty);
        UpdateShaderValue(FadeOpacityProperty);
        UpdateShaderValue(KeyEnabledProperty);
    }
}
