using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OraPkgMap
{
    public partial class Main : Form
    {
        public Main()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            var cg = new CodeGen();
            textBox5.Text = cg.CreateClass(textBox1.Text, textBox2.Text.ToUpper(), textBox3.Text.ToUpper(), textBox4.Text);
        }
    }
}
