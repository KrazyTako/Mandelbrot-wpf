using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace WpfApp1
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // Semaphore to prevent firing off the drawing of the mandelbrot while it is being drawn
        SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);
        Stack<Image> imageStack = new Stack<Image>();
        Stack<Tuple<double, double, int>> offsetStack = new Stack<Tuple<double, double, int>>();
        CancellationTokenSource tokenSource = new CancellationTokenSource();
        PixelShader shader = new PixelShader();
        WriteableBitmap bitmap;

        Canvas canvas;
        GroupBox zoomHeader;
        Label timerLabel;
        Label xPositionLabel;
        Label yPositionLabel;
        Label zoomLabel;
        Slider slider;
        ProgressBar progressBar;

        const int MAX = 512;
        double zoom = 1;
        double xOffset = 0;
        double yOffset = 0;
        double yMin = -2;
        double xMin = -2;

        public MainWindow()
        {
            InitializeComponent();
            canvas = (Canvas)FindName("Canvas");
            timerLabel = (Label)FindName("TimerLabel");
            xPositionLabel = (Label)FindName("XPosLabel");
            yPositionLabel = (Label)FindName("YPosLabel");
            slider = (Slider)FindName("Slider");
            zoomLabel = (Label)FindName("ZoomLabel");
            progressBar = (ProgressBar)FindName("ProgressBar");
            zoomHeader = (GroupBox)FindName("ZoomHeader");
            Console.WriteLine("HORAY!");
        }

        private async void Button_Click(object sender, RoutedEventArgs e)
        {
            zoom = 1;
            xOffset = 0;
            yOffset = 0;
            imageStack.Clear();
            offsetStack.Clear();
            await semaphore.WaitAsync();
            await PaintMandelbrot();
        }

        private async Task PaintMandelbrot()
        {
            var token = tokenSource.Token;
            Stopwatch timer = new Stopwatch();
            timer.Start();

            int pixelWidth = (int)canvas.ActualWidth;
            int pixelHeight = (int)canvas.ActualHeight;

            IProgress<double> progress = new Progress<double>(value => progressBar.Value += value);
            progressBar.Value = 0;
            progressBar.Maximum = pixelHeight;

            bitmap = new WriteableBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Bgr32, null);

            int bytesPerPixel = bitmap.Format.BitsPerPixel / 8;
            int widthInBytes = pixelWidth * bytesPerPixel;
            int stride = pixelWidth * bytesPerPixel;

            byte[] pixels = new byte[pixelHeight * stride];

            double widthDiv2 = bitmap.Width / 2.0;
            double widthXzoom = bitmap.Width * zoom;
            double heightDiv2 = bitmap.Height / 2.0;
            double heightXzoom = bitmap.Height * zoom;

            try
            {
                await Task.Run(() =>
                {
                    var options = new ParallelOptions();
                    options.CancellationToken = token;
                    Parallel.For(0, pixelHeight, options, row =>
                    {
                        int currentLine = row * stride;
                        long colxx = 0;
                        int iter = widthInBytes / 4;
                        for (int col = 0; col < widthInBytes; col += bytesPerPixel)
                        {
                            double c_re = ((colxx - widthDiv2) * 4.0 / widthXzoom) + xOffset;
                            double c_im = ((row - heightDiv2) * 4.0 / heightXzoom) + yOffset;
                            double x = 0, y = 0;
                            int iteration = 0;
                            while (x * x + y * y <= 4 && iteration < MAX)
                            {
                                double x_new = x * x - y * y + c_re;
                                y = 2 * x * y + c_im;
                                x = x_new;
                                iteration++;
                            }

                            colxx++;
                            Color myColor = Color.FromRgb((byte)(iteration % 255), (byte)((iteration + 20) % 255), (byte)((iteration + 10) % 255));
                            pixels[currentLine] = myColor.B;
                            pixels[currentLine + col + 1] = myColor.G;
                            pixels[currentLine + col + 2] = myColor.R;
                        }
                        progress.Report(1.0);
                    });
                }, token);
             }
            catch (OperationCanceledException e)
            {
                semaphore.Release();
                return;
            }

            timer.Stop();
            bitmap.WritePixels(new Int32Rect(0, 0, pixelWidth, pixelHeight), pixels, stride, 0);
            Image waveform = new Image();
            waveform.Source = bitmap;
            //imageStack.Push(waveform);
            Canvas.Children.Clear();
            Canvas.Children.Add(waveform);
            timerLabel.Content = $"{Math.Round(timer.Elapsed.TotalSeconds, 3)} s";
            semaphore.Release();
        }

        private async void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            Point position = e.GetPosition(canvas);
            double centerX = (xMin + 4.0 * (position.X / canvas.ActualWidth)) / zoom;
            double centerY = (yMin + 4.0 * (position.Y / canvas.ActualHeight)) / zoom;
            double lastX = double.Parse(xPositionLabel.Content.ToString());
            double lastY = double.Parse(yPositionLabel.Content.ToString());
            xPositionLabel.Content = centerX.ToString();
            yPositionLabel.Content = centerY.ToString();

            if (e.LeftButton == MouseButtonState.Pressed)
            {
                double deltaX = lastX - centerX;
                double deltaY = lastY - centerY;
                xOffset += deltaX;
                yOffset += deltaY;
                if (tokenSource.IsCancellationRequested)
                {
                    return;
                }
                if (semaphore.CurrentCount == 0)
                {
                    tokenSource.Cancel();
                }
                await semaphore.WaitAsync();
                await PaintMandelbrot();
                tokenSource = new CancellationTokenSource();
                //xPositionLabel.Content = deltaX.ToString();
                //yPositionLabel.Content = deltaY.ToString();
            }
            else
            {
                Mouse.OverrideCursor = Cursors.Arrow;
            }
        }

        private async void Canvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (tokenSource.IsCancellationRequested)
            {
                return;
            }
            if (semaphore.CurrentCount == 0)
            {
                tokenSource.Cancel();
            }
            await semaphore.WaitAsync();
            await PaintMandelbrot();
            tokenSource = new CancellationTokenSource();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            BitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using (var filestream = new FileStream("thing.png", FileMode.Create))
            {
                encoder.Save(filestream);
            }
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            if (imageStack.Count == 1 || offsetStack.Count == 0)
            {
                return;
            }
            imageStack.Pop();
            Image last = imageStack.Pop();
            Canvas.Children.Clear();
            Canvas.Children.Add(last);
            var offset = offsetStack.Pop();
            xOffset -= offset.Item1;
            yOffset -= offset.Item2;
            zoom /= offset.Item3;
            imageStack.Push(last);
        }

        private async void Canvas_Loaded(object sender, RoutedEventArgs e) { await semaphore.WaitAsync(); await PaintMandelbrot(); }
        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e) => Mouse.OverrideCursor = Cursors.ScrollAll;
        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var thingy = (Slider)sender;
            if (thingy.IsLoaded)
                zoomHeader.Header = $"Zoom: {e.NewValue}";
        }

        private async void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var delta = e.Delta;
            if (semaphore.CurrentCount > 0)
            {
                if(Math.Sign(delta) > 0)
                    zoom *= (long)slider.Value;
                else
                    zoom /= (long)slider.Value;

                double xOld = double.Parse(xPositionLabel.Content.ToString());
                double yOld = double.Parse(yPositionLabel.Content.ToString());
                offsetStack.Push(Tuple.Create(xOld, yOld, (int)slider.Value));
                xOffset += xOld;
                yOffset += yOld;
                Point mouseP = e.GetPosition(canvas);
                var rect = new Rectangle() { Width = 100, Height = 100 };
                await semaphore.WaitAsync();
                await PaintMandelbrot();
            }
        }
    }
}