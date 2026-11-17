using System;
using System.Windows.Forms;
using v2rayN.Mode;
using v2rayN.Resx;

namespace v2rayN.Forms
{
    public partial class BaseServerForm : BaseForm
    {
        public VmessItem vmessItem = null;
        public string groupId;
        public EConfigType eConfigType;

        public BaseServerForm()
        {
            InitializeComponent();
        }

        protected string[] GetCoreTypeOptions()
        {
            return new string[]
            {
                ResUI.ResourceManager.GetString("CoreTypeV2Ray5"),
                ResUI.ResourceManager.GetString("CoreTypeV2RayLegacy"),
                ResUI.ResourceManager.GetString("CoreTypeXray1"),
                ResUI.ResourceManager.GetString("CoreTypeXrayLegacy"),
                "SagerNet"
            };
        }

        protected int CoreTypeToOptionIndex(ECoreType coreType)
        {
            switch (coreType)
            {
                case ECoreType.v2fly_v5: return 0;
                case ECoreType.v2fly: return 1;
                case ECoreType.Xray: return 2;
                case ECoreType.Xray_legacy: return 3;
                case ECoreType.SagerNet: return 4;
                default: return -1;
            }
        }

        protected ECoreType CoreTypeFromOptionIndex(int index)
        {
            switch (index)
            {
                case 0: return ECoreType.v2fly_v5;
                case 1: return ECoreType.v2fly;
                case 2: return ECoreType.Xray;
                case 3: return ECoreType.Xray_legacy;
                case 4: return ECoreType.SagerNet;
                default: return ECoreType.Xray;
            }
        }

        protected void InitCoreTypeCombo(ComboBox combo, ECoreType? coreType)
        {
            combo.Items.Clear();
            combo.Items.AddRange(GetCoreTypeOptions());
            combo.Items.Add(string.Empty);
            combo.DropDownWidth = 230;
            combo.SelectedIndex = coreType.HasValue ? CoreTypeToOptionIndex(coreType.Value) : combo.Items.Count - 1;
        }

        protected ECoreType? GetSelectedCoreType(ComboBox combo)
        {
            if (combo.SelectedIndex < 0 || combo.SelectedIndex == combo.Items.Count - 1)
            {
                return null;
            }
            return CoreTypeFromOptionIndex(combo.SelectedIndex);
        }
    }
}
