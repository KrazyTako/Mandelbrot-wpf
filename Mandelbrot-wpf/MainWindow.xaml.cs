using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WpfApp1
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // Semaphore to prevent firing off the drawing of the mandelbrot while it is being drawn
        SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);
        CancellationTokenSource tokenSource = new CancellationTokenSource();

        const double xMin = -2;
        const double yMin = -2;
        int maxIterations = 256;
        double zoom = 1;
        double xOffset = 0;
        double yOffset = 0;
        double relativeMouseX = 0.0;
        double relativeMouseY = 0.0;
        double lastMouseRenderX = 0;
        double lastMouseRenderY = 0;

        public MainWindow() => InitializeComponent();
        private async void Canvas_SizeChanged(object sender, SizeChangedEventArgs e) => await PaintMandelbrot();

        private async void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            zoom = 1;
            xOffset = 0;
            yOffset = 0;
            await PaintMandelbrot();
        }

        private async void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (semaphore.CurrentCount > 0)
            {
                if (Math.Sign(e.Delta) > 0)
                    zoom *= Slider.Value;
                else
                    zoom /= Slider.Value;

                xOffset += relativeMouseX;
                yOffset += relativeMouseY;

                await PaintMandelbrot();
            }
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            lastMouseRenderX = relativeMouseX;
            lastMouseRenderY = relativeMouseY;
            Mouse.OverrideCursor = Cursors.ScrollAll;
        }

        private async void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            Mouse.OverrideCursor = Cursors.Arrow;
            double deltaX = lastMouseRenderX - relativeMouseX;
            double deltaY = lastMouseRenderY - relativeMouseY;
            xOffset += deltaX;
            yOffset += deltaY;
            await PaintMandelbrot();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            Point position = e.GetPosition(Canvas);
            relativeMouseX = (xMin + 4.0 * (position.X / Canvas.ActualWidth)) / zoom;
            relativeMouseY = (yMin + 4.0 * (position.Y / Canvas.ActualHeight)) / zoom;
            XPosLabel.Content = (relativeMouseX + xOffset).ToString();
            YPosLabel.Content = (relativeMouseY + yOffset).ToString();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string fileName = FileNameTextBox.Text;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                MessageBox.Show("Please enter a file name for the image", "Missing name", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (char item in Path.GetInvalidFileNameChars())
            {
                if (fileName.Contains(item.ToString()))
                {
                    MessageBox.Show("Invalid file name.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            BitmapEncoder encoder = new PngBitmapEncoder();
            BitmapSource bitmapSrc = MandelbrotImage.Source as BitmapSource;
            encoder.Frames.Add(BitmapFrame.Create(bitmapSrc));

            using (var filestream = new FileStream($"{fileName}.png", FileMode.Create))
            {
                encoder.Save(filestream);
                MessageBox.Show($"Saved image {fileName}.png to bin.", "Success!");
                FileNameTextBox.Clear();
            }
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var thingy = (Slider)sender;
            if (thingy.IsLoaded)
                ZoomHeader.Header = $"Zoom: {e.NewValue}";
        }

        private async Task PaintMandelbrot()
        {
            if (!int.TryParse(MaxIterationsTextBox.Text, out maxIterations))
            {
                MessageBox.Show("Invalid max iterations entered. Please provide a number", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if(maxIterations > 10000)
            {
                MessageBox.Show("Invalid max iterations entered. Please provide a number between 0 - 10000", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (tokenSource.IsCancellationRequested)
                return;

            if (semaphore.CurrentCount == 0)
                tokenSource.Cancel();

            await semaphore.WaitAsync();

            Stopwatch timer = new Stopwatch();
            timer.Start();

            MandelbrotImage.Source = await GenerateBitmap();
            timer.Stop();

            TimerLabel.Content = $"{Math.Round(timer.Elapsed.TotalSeconds, 3)} s";
            semaphore.Release();
        }

        private async Task<BitmapSource> GenerateBitmap()
        {
            int pixelWidth = (int)Canvas.ActualWidth;
            int pixelHeight = (int)Canvas.ActualHeight;

            IProgress<double> progress = new Progress<double>(value => ProgressBar.Value += value);
            ProgressBar.Value = 0;
            ProgressBar.Maximum = pixelHeight;

            WriteableBitmap bitmap = new WriteableBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Bgr32, null);

            int bytesPerPixel = bitmap.Format.BitsPerPixel / 8;
            int widthInBytes = pixelWidth * bytesPerPixel;
            int stride = pixelWidth * bytesPerPixel;

            byte[] pixels = new byte[pixelHeight * stride];

            double widthDiv2 = bitmap.Width / 2.0;
            double widthXzoom = bitmap.Width * zoom;
            double heightDiv2 = bitmap.Height / 2.0;
            double heightXzoom = bitmap.Height * zoom;

            await Task.Run(() =>
            {
                var options = new ParallelOptions() { CancellationToken = tokenSource.Token };
                try
                {
                    Parallel.For(0, pixelHeight, options, row =>
                    {
                        int currentLine = row * stride;
                        long colxx = 0;
                        for (int col = 0; col < widthInBytes; col += bytesPerPixel)
                        {
                            double c_re = ((colxx - widthDiv2) * 4.0 / widthXzoom) + xOffset;
                            double c_im = ((row - heightDiv2) * 4.0 / heightXzoom) + yOffset;
                            double x = 0, y = 0;
                            int iteration = 0;
                            while (x * x + y * y <= 4 && iteration < maxIterations)
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
                }
                catch (OperationCanceledException)
                {
                    tokenSource = new CancellationTokenSource();
                }
            });

            bitmap.WritePixels(new Int32Rect(0, 0, pixelWidth, pixelHeight), pixels, stride, 0);
            return bitmap;
        }

        private void MaxIterationsTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!char.IsDigit(e.Text, e.Text.Length - 1))
                e.Handled = true;
        }

        private async void MaxIterationsTextBox_KeyUp(object sender, KeyEventArgs e) => await PaintMandelbrot();
    }
}