namespace WinFormsRTC
{
    public partial class Form1 : Form
    {
        WebRTC webRTC=new WebRTC();

        public Form1()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            webRTC.StartCall();
        }
    }
}
