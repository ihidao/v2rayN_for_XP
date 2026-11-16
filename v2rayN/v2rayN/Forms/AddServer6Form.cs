using System;
using System.Windows.Forms;
using v2rayN.Handler;
using v2rayN.Mode;

namespace v2rayN.Forms
{
    public partial class AddServer6Form : BaseServerForm
    {
        public AddServer6Form()
        {
            InitializeComponent();
        }

        private void AddServer6Form_Load(object sender, EventArgs e)
        {
            if (EditIndex >= 0)
            {
                vmessItem = config.vmess[EditIndex];
                BindingServer();
            }
            else
            {
                vmessItem = new VmessItem();
                ClearServer();
            }
        }

        /// <summary>
        /// 绑定数据
        /// </summary>
        private void BindingServer()
        {

            txtAddress.Text = vmessItem.address;
            txtPort.Text = vmessItem.port.ToString();
            txtId.Text = vmessItem.id;
            txtSNI.Text = vmessItem.sni;
            cmbNetwork.Text = vmessItem.network;
            cmbHeaderType.Text = vmessItem.headerType;
            txtRequestHost.Text = vmessItem.requestHost;
            txtPath.Text = vmessItem.path;
            txtRemarks.Text = vmessItem.remarks;
            cmbStreamSecurity.Text = vmessItem.streamSecurity;
            cmbAllowInsecure.Text = vmessItem.allowInsecure;
        }


        /// <summary>
        /// 清除设置
        /// </summary>
        private void ClearServer()
        {
            txtAddress.Text = "";
            txtPort.Text = "";
            txtId.Text = "";
            txtSNI.Text = "";
            cmbNetwork.Text = Global.DefaultNetwork;
            SetHeaderType();
            txtRequestHost.Text = "";
            txtPath.Text = "";
            txtRemarks.Text = ""; 
            cmbStreamSecurity.Text = "tls";
            cmbAllowInsecure.Text = "";
        }

        private void cmbNetwork_SelectedIndexChanged(object sender, EventArgs e)
        {
            SetHeaderType();
        }

        private void SetHeaderType()
        {
            cmbHeaderType.Items.Clear();
            string network = cmbNetwork.Text;
            if (Utils.IsNullOrEmpty(network))
            {
                cmbHeaderType.Items.Add(Global.None);
            }
            else if (network.Equals(Global.DefaultNetwork))
            {
                cmbHeaderType.Items.Add(Global.None);
                cmbHeaderType.Items.Add(Global.TcpHeaderHttp);
            }
            else if (network.Equals("kcp") || network.Equals("quic"))
            {
                cmbHeaderType.Items.Add(Global.None);
                cmbHeaderType.Items.Add("srtp");
                cmbHeaderType.Items.Add("utp");
                cmbHeaderType.Items.Add("wechat-video");
                cmbHeaderType.Items.Add("dtls");
                cmbHeaderType.Items.Add("wireguard");
            }
            else if (network.Equals("grpc"))
            {
                cmbHeaderType.Items.Add(Global.GrpcgunMode);
                cmbHeaderType.Items.Add(Global.GrpcmultiMode);
            }
            else
            {
                cmbHeaderType.Items.Add(Global.None);
            }
            cmbHeaderType.SelectedIndex = 0;
        }

        private void btnOK_Click(object sender, EventArgs e)
        {
            string address = txtAddress.Text;
            string port = txtPort.Text;
            string id = txtId.Text;
            string sni = txtSNI.Text;
            string network = cmbNetwork.Text;
            string headerType = cmbHeaderType.Text;
            string requestHost = txtRequestHost.Text;
            string path = txtPath.Text;
            string remarks = txtRemarks.Text;
            string streamSecurity = cmbStreamSecurity.Text;
            string allowInsecure = cmbAllowInsecure.Text;

            if (Utils.IsNullOrEmpty(address))
            {
                UI.Show(UIRes.I18N("FillServerAddress"));
                return;
            }
            if (Utils.IsNullOrEmpty(port) || !Utils.IsNumberic(port))
            {
                UI.Show(UIRes.I18N("FillCorrectServerPort"));
                return;
            }
            if (Utils.IsNullOrEmpty(id))
            {
                UI.Show(UIRes.I18N("FillPassword"));
                return;
            } 

            vmessItem.address = address;
            vmessItem.port = Utils.ToInt(port);
            vmessItem.id = id;
            vmessItem.sni = sni.Replace(" ", "");
            vmessItem.network = network;
            vmessItem.headerType = headerType;
            vmessItem.requestHost = requestHost.Replace(" ", "");
            vmessItem.path = path.Replace(" ", "");
            vmessItem.remarks = remarks;
            vmessItem.streamSecurity = streamSecurity;
            vmessItem.allowInsecure = allowInsecure;

            if (ConfigHandler.AddTrojanServer(ref config, vmessItem, EditIndex) == 0)
            {
                this.DialogResult = DialogResult.OK;
            }
            else
            {
                UI.ShowWarning(UIRes.I18N("OperationFailed"));
            }
        }
        private void btnClose_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
        }

    }
}
