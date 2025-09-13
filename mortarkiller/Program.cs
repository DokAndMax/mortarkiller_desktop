using System;
using System.Linq;
using System.Windows.Forms;
using WinFormsApp1;

namespace mortarkiller;

static class Program
{
    /// <summary>
    /// Главная точка входа для приложения.
    /// </summary>
    [STAThread]
    static void Main()
    {
        //ProgramUnified.Run([.. Environment.GetCommandLineArgs().Skip(1)]).GetAwaiter().GetResult();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Form1());
    }
}
