using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Nexo.Setup;

public sealed class SetupWindow : Window
{
    private readonly string[] args;
    private readonly StackPanel content = new() { Margin = new Thickness(28, 24, 28, 12) };
    private readonly TextBlock heading = new() { FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black };
    private readonly TextBlock subheading = new() { FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(72, 78, 88)), Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock status = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(65, 72, 82)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, 12, 28, 0) };
    private readonly Button next = new() { Content = "Próximo >", Width = 112, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button cancel = new() { Content = "Cancelar", Width = 90, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
    private int page;
    private bool installing;
    public int ExitCode { get; private set; } = 1;

    public SetupWindow(string[] args)
    {
        this.args = args;
        Title = "Instalação do Nexo";
        Width = 590;
        Height = 425;
        MinWidth = MaxWidth = Width;
        MinHeight = MaxHeight = Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.White;

        var frame = new DockPanel();
        var banner = new Grid { Height = 80, Background = new SolidColorBrush(Color.FromRgb(247, 249, 252)) };
        banner.ColumnDefinitions.Add(new ColumnDefinition());
        banner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock { Text = "✦  Nexo", FontSize = 27, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(35, 58, 110)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(26, 0, 0, 0) };
        banner.Children.Add(name);
        var mark = new Border { Background = new SolidColorBrush(Color.FromRgb(129, 163, 235)), CornerRadius = new CornerRadius(9), Width = 42, Height = 42, Margin = new Thickness(0, 0, 26, 0), VerticalAlignment = VerticalAlignment.Center };
        mark.Child = new TextBlock { Text = "N", Foreground = Brushes.White, FontSize = 24, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(mark, 1); banner.Children.Add(mark);
        DockPanel.SetDock(banner, Dock.Top); frame.Children.Add(banner);

        var footer = new DockPanel { Height = 58, Background = new SolidColorBrush(Color.FromRgb(247, 249, 252)), LastChildFill = false };
        var version = new TextBlock { Text = "Nexo · instalador online", FontSize = 11, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(28, 0, 0, 0) };
        DockPanel.SetDock(version, Dock.Left); footer.Children.Add(version);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 26, 0) };
        next.Click += NextClick;
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(next); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); frame.Children.Add(footer);

        var body = new DockPanel();
        DockPanel.SetDock(status, Dock.Bottom); body.Children.Add(status);
        body.Children.Add(content);
        frame.Children.Add(body);
        Content = frame;
        Closing += (_, eventArgs) => { if (installing) eventArgs.Cancel = true; };
        RenderPage();
    }

    private void RenderPage()
    {
        content.Children.Clear();
        if (page == 0)
        {
            heading.Text = "Escolha as opções de instalação";
            subheading.Text = "Para quem o Nexo ficará disponível neste computador?";
            content.Children.Add(heading); content.Children.Add(subheading);
            content.Children.Add(new TextBlock { Text = "O aplicativo e o modelo local serão instalados para a sua conta do Windows.", Margin = new Thickness(0, 28, 0, 19), TextWrapping = TextWrapping.Wrap, FontSize = 13 });
            content.Children.Add(new RadioButton { Content = "Somente para mim (recomendado)", IsChecked = true, FontSize = 13, Margin = new Thickness(12, 0, 0, 16) });
            content.Children.Add(new RadioButton { Content = "Qualquer pessoa que use este computador", IsEnabled = false, FontSize = 13, Margin = new Thickness(12, 0, 0, 8), ToolTip = "O Ollama e o modelo incluídos nesta versão são instalados por usuário." });
            content.Children.Add(new TextBlock { Text = "A instalação para todos os usuários requer um serviço de modelo compartilhado e ainda não está disponível nesta versão.", Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(32, 0, 8, 0) });
            next.Content = "Próximo >";
        }
        else if (page == 1)
        {
            heading.Text = "Pronto para instalar";
            subheading.Text = "O instalador vai baixar a versão publicada e o modelo de IA.";
            content.Children.Add(heading); content.Children.Add(subheading);
            content.Children.Add(new TextBlock { Text = "Destino: " + System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Nexo")
                + "\n\nInclui o Nexo, o Ollama e o modelo qwen3:8b. Os pacotes são conferidos por assinatura e SHA-256 antes do uso. É necessária conexão com a internet e espaço livre para o modelo.",
                FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 30, 6, 0) });
            next.Content = "Instalar";
        }
        else
        {
            heading.Text = installing ? "Instalando o Nexo" : ExitCode == 0 ? "Instalação concluída" : "Instalação interrompida";
            subheading.Text = installing ? "Aguarde enquanto os pacotes são baixados e preparados." : ExitCode == 0 ? "O Nexo está disponível no menu Iniciar." : "Leia o detalhe abaixo e tente novamente.";
            content.Children.Add(heading); content.Children.Add(subheading);
            var bar = new ProgressBar { IsIndeterminate = installing, Height = 12, Margin = new Thickness(0, 45, 0, 0), Value = ExitCode == 0 ? 100 : 0, Maximum = 100 };
            content.Children.Add(bar);
            next.Visibility = installing ? Visibility.Collapsed : Visibility.Visible;
            next.Content = ExitCode == 0 ? "Concluir" : "Tentar novamente";
            cancel.Visibility = installing ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private async void NextClick(object sender, RoutedEventArgs e)
    {
        if (page == 0) { page = 1; RenderPage(); return; }
        if (page == 2 && ExitCode == 0) { Close(); return; }
        page = 2; installing = true; status.Text = "Consultando a versão publicada…"; RenderPage();
        ExitCode = await Task.Run(() => Installer.RunAsync(args, message => Dispatcher.BeginInvoke(() => status.Text = message), false));
        installing = false; RenderPage();
    }
}
