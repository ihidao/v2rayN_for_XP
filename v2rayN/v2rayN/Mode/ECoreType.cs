
namespace v2rayN.Mode
{
    // Preserve numeric values 0=V2Ray and 1=Xray for existing config files.
    public enum ECoreType
    {
        v2ray_core_5_or_above = 0,
        xray_core_1_or_above = 1,
        v2ray_core_below_5 = 2,
        xray_core_below_1 = 3
    }
}
