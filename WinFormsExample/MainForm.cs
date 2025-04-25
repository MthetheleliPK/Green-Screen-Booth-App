using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Windows.Forms;
using EOSDigital.API;
using EOSDigital.SDK;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;

namespace WinFormsExample
{
    public partial class MainForm  : Form
    {
        #region Variables

        CanonAPI APIHandler;
        Camera MainCamera;
        CameraValue[] AvList;
        CameraValue[] TvList;
        CameraValue[] ISOList;
        List<Camera> CamList;
        bool IsInit = false;
        Bitmap Evf_Bmp;
        int LVBw, LVBh, w, h;
        float LVBratio, LVration;

        int ErrCount;
        object ErrLock = new object();
        object LvLock = new object();
        private string imageFolderPath = @"C:\templates"; // Folder path
        private PictureBox selectedTemplate = null;

        private System.Windows.Forms.Timer countdownTimer;
        private int countdownValue = 0;
        private Label countdownLabel;

        private Bitmap lastCapturedImage = null;
        private System.Windows.Forms.Timer capturedImageTimer;
        private Bitmap selectedTemplateImage = null;

        // Add these new variables to the #region Variables section
        private PrintDocument printDocument;


        #endregion

        public MainForm()
        {
            try
            {
                InitializeComponent();
                LoadImagesIntoPictureBoxes();
                APIHandler = new CanonAPI();
                APIHandler.CameraAdded += APIHandler_CameraAdded;
                ErrorHandler.SevereErrorHappened += ErrorHandler_SevereErrorHappened;
                ErrorHandler.NonSevereErrorHappened += ErrorHandler_NonSevereErrorHappened;
                SavePathTextBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RemotePhoto");
                SaveFolderBrowser.Description = "Save Images To...";
                LiveViewPicBox.Paint += LiveViewPicBox_Paint;
                LVBw = LiveViewPicBox.Width;
                LVBh = LiveViewPicBox.Height;
                RefreshCamera();
                IsInit = true;

                countdownTimer = new System.Windows.Forms.Timer();
                countdownTimer.Interval = 1000; // 1 second
                countdownTimer.Tick += CountdownTimer_Tick;

                countdownLabel = new Label();
                countdownLabel.TextAlign = ContentAlignment.MiddleCenter;
                countdownLabel.Font = new Font("Arial", 48, FontStyle.Bold);
                countdownLabel.ForeColor = Color.Red;
                countdownLabel.Visible = false;
                countdownLabel.Size = LiveViewPicBox.Size;
                LiveViewPicBox.Controls.Add(countdownLabel);

                capturedImageTimer = new System.Windows.Forms.Timer();
                capturedImageTimer.Interval = 5000; // 5 seconds
                capturedImageTimer.Tick += (s, args) =>
                {
                    lastCapturedImage?.Dispose();
                    lastCapturedImage = null;
                    LiveViewPicBox.Invalidate();
                    capturedImageTimer.Stop();
                };

            }
            catch (DllNotFoundException) { ReportError("Canon DLLs not found!", true); }
            catch (Exception ex) { ReportError(ex.Message, true); }
        }

        private void LoadImagesIntoPictureBoxes()
        {
            if (!Directory.Exists(imageFolderPath))
            {
                MessageBox.Show("Folder not found: " + imageFolderPath, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Get up to 6 image files from the folder
            string[] imageFiles = Directory.GetFiles(imageFolderPath, "*.*")
                                           .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                                       f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                                       f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                                       f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
                                                       f.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                                           .Take(6) // Load only the first 6 images
                                           .ToArray();

            if (imageFiles.Length == 0)
            {
                MessageBox.Show("No images found in the folder.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Assign images to PictureBoxes
            PictureBox[] pictureBoxes = { pictureBox2, pictureBox3, pictureBox4, pictureBox5 };
            for (int i = 0; i < imageFiles.Length; i++)
            {
                try
                {
                    Bitmap bmp = new Bitmap(imageFiles[i]);
                    pictureBoxes[i].Image = bmp;
                    pictureBoxes[i].SizeMode = PictureBoxSizeMode.StretchImage;
                    pictureBoxes[i].Click += Template_Click;
                    pictureBoxes[i].BorderStyle = BorderStyle.None;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading image: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void Template_Click(object sender, EventArgs e)
        {
            if (selectedTemplate != null)
            {
                selectedTemplate.BorderStyle = BorderStyle.None;
            }

            selectedTemplate = sender as PictureBox;
            selectedTemplate.BorderStyle = BorderStyle.Fixed3D;

            if (selectedTemplate.Image != null)
            {
                selectedTemplateImage?.Dispose();
                selectedTemplateImage = new Bitmap(selectedTemplate.Image);
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                IsInit = false;
                MainCamera?.Dispose();
                APIHandler?.Dispose();
            }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        #region API Events
        
        private void APIHandler_CameraAdded(CanonAPI sender)
        {
            try { Invoke((Action)delegate { RefreshCamera(); }); }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        private void MainCamera_StateChanged(Camera sender, StateEventID eventID, int parameter)
        {
            try { if (eventID == StateEventID.Shutdown && IsInit) { Invoke((Action)delegate { CloseSession(); }); } }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }
        
        private void MainCamera_ProgressChanged(object sender, int progress)
        {
            try { Invoke((Action)delegate { MainProgressBar.Value = progress; }); }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        private void MainCamera_LiveViewUpdated(Camera sender, Stream img)
        {
            try
            {
                lock (LvLock)
                {
                    Evf_Bmp?.Dispose();
                    Evf_Bmp = new Bitmap(img);
                }
                LiveViewPicBox.Invalidate();
            }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        private void MainCamera_DownloadReady(Camera sender, DownloadInfo Info)
        {
            try
            {
                string dir = null;
                Bitmap template = null;
                Invoke((Action)delegate {
                    dir = SavePathTextBox.Text;
                    template = selectedTemplateImage;
                });

                sender.DownloadFile(Info, dir);

                // Process the image after download
                var imagePath = Path.Combine(dir, Info.FileName);
                Task.Run(() => ProcessDownloadedImage(imagePath, template, dir));

                Invoke((Action)delegate { MainProgressBar.Value = 0; });
            }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        // Fallback method that processes at reduced size if full-size processing fails

        private void EmailButton_Click(object sender, EventArgs e)
        {
            if (lastCapturedImage == null)
            {
                MessageBox.Show("No image captured to send.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var dialog = new Form
            {
                Text = "Send Email",
                Size = new System.Drawing.Size(300, 150),
                StartPosition = FormStartPosition.CenterParent
            })
            {
                var emailBox = new TextBox { Width = 250, Top = 20, Left = 20 };
                var sendButton = new Button { Text = "Send", Top = 60, Left = 20 };

                sendButton.Click += (s, args) =>
                {
                    if (IsValidEmail(emailBox.Text))
                    {
                        SendEmail(emailBox.Text, lastCapturedImage);
                        dialog.Close();
                    }
                    else
                    {
                        MessageBox.Show("Invalid email address!");
                    }
                };

                dialog.Controls.Add(emailBox);
                dialog.Controls.Add(sendButton);
                dialog.ShowDialog();
            }
        }

        private void SendEmail(string recipient, Bitmap image)
        {
            try
            {
                // Save Bitmap to a temporary file (just like image_path in Python)
                string tempImagePath = Path.Combine(Path.GetTempPath(), "photo.jpg");
                image.Save(tempImagePath, ImageFormat.Jpeg);

                // Create the email
                MailMessage message = new MailMessage();
                message.From = new MailAddress("automation@sansa.org.za");
                message.To.Add(recipient);
                message.Subject = "Your Magical Photo!";
                message.Body = "Here's your magical photo from the Photo Booth! Thank You for visiting SANSA ✨";
                message.IsBodyHtml = false;

                // Attach the image file
                Attachment attachment = new Attachment(tempImagePath);
                message.Attachments.Add(attachment);

                // Configure SMTP client
                SmtpClient smtpClient = new SmtpClient("relay3.sansa.org.za", 25);
                smtpClient.DeliveryMethod = SmtpDeliveryMethod.Network;
                smtpClient.UseDefaultCredentials = true; // No username or password
                smtpClient.EnableSsl = false; // No SSL like in Python

                // Send the email
                smtpClient.Send(message);

                MessageBox.Show("Email sent successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to send email: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PrintButton_Click(object sender, EventArgs e)
        {
            if (lastCapturedImage == null)
            {
                MessageBox.Show("No image to print.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            printDocument = new PrintDocument();
            printDocument.PrintPage += (s, ev) =>
            {
                ev.Graphics.DrawImage(lastCapturedImage, ev.MarginBounds);
            };

            using (PrintDialog printDialog = new PrintDialog { Document = printDocument })
            {
                if (printDialog.ShowDialog() == DialogResult.OK)
                {
                    printDocument.Print();
                }
            }
        }

        private bool IsValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }

        private void ErrorHandler_NonSevereErrorHappened(object sender, ErrorCode ex)
        {
            ReportError($"SDK Error code: {ex} ({((int)ex).ToString("X")})", false);
        }

        private void ErrorHandler_SevereErrorHappened(object sender, Exception ex)
        {
            ReportError(ex.Message, true);
        }

        #endregion

        #region Session

        private void SessionButton_Click(object sender, EventArgs e)
        {
            if (MainCamera?.SessionOpen == true) CloseSession();
            else OpenSession();
        }

        private void RefreshButton_Click(object sender, EventArgs e)
        {
            try { RefreshCamera(); }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        #endregion

        #region Settings

        private async Task ProcessDownloadedImage(string imagePath, Bitmap template, string saveDir)
        {
            if (!File.Exists(imagePath)) return;

            HttpClient client = null;
            Stream responseStream = null;
            FileStream outputFile = null;
            Bitmap resultImage = null;

            try
            {
                client = new HttpClient();

                // Step 1: Send image to rembg server
                var content = new MultipartFormDataContent();
                var imageData = new StreamContent(File.OpenRead(imagePath));
                imageData.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(imagePath));
                content.Add(imageData, "image", Path.GetFileName(imagePath));

                var response = await client.PostAsync("http://localhost:5001/remove", content);
                if (!response.IsSuccessStatusCode)
                {
                    string error = await response.Content.ReadAsStringAsync();
                    throw new Exception("Rembg failed: " + error);
                }

                responseStream = await response.Content.ReadAsStreamAsync();
                using (var cutout = new Bitmap(responseStream))
                {
                    // Resize template to match cutout
                    using (var resizedTemplate = new Bitmap(template, cutout.Width, cutout.Height))
                    {
                        resultImage = new Bitmap(cutout.Width, cutout.Height);
                        using (Graphics g = Graphics.FromImage(resultImage))
                        {
                            g.DrawImage(resizedTemplate, 0, 0);    // Background
                            g.DrawImage(cutout, 0, 0);             // Foreground
                        }
                    }

                    // Save with timestamp
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string fileName = $"photo_{timestamp}.jpg";
                    string outputPath = Path.Combine(saveDir, fileName);

                    outputFile = File.Create(outputPath);
                    resultImage.Save(outputFile, ImageFormat.Jpeg);

                    // Delete original image
                    File.Delete(imagePath);

                    // Update UI
                    Invoke((Action)(() =>
                    {
                        capturedImageTimer.Stop();
                        lastCapturedImage?.Dispose();
                        lastCapturedImage = new Bitmap(resultImage);
                        LiveViewPicBox.Invalidate();
                    }));
                }
            }
            catch (Exception ex)
            {
                ReportError("Error during background removal: " + ex.Message, false);
            }
            finally
            {
                outputFile?.Dispose();
                responseStream?.Dispose();
                client?.Dispose();
                resultImage?.Dispose();
            }
        }

        // Add this helper method to the MainForm class
        private string GetMimeType(string filePath)
        {
            switch (Path.GetExtension(filePath).ToLowerInvariant())
            {
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                default: return "application/octet-stream";
            }
        }

        private void TakePhotoButton_Click(object sender, EventArgs e)
        {
            try
            {
                if (selectedTemplateImage == null)
                {
                    MessageBox.Show("Please select a template before taking a photo.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                countdownTimer.Stop();
                countdownValue = 5;
                countdownLabel.Text = "5"; // Immediately show 5
                countdownLabel.Visible = true;
                countdownLabel.BringToFront();

                countdownTimer.Start(); // Start the existing timer
            }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        private void CountdownTimer_Tick(object sender, EventArgs e)
        {
            countdownLabel.Font = new Font("Arial", 96, FontStyle.Bold);
            countdownLabel.ForeColor = countdownValue % 2 == 0 ? Color.Red : Color.Orange;
            countdownLabel.Text = countdownValue.ToString();
            countdownLabel.Refresh();
            countdownValue--;

            countdownLabel.Location = new System.Drawing.Point(
                (LiveViewPicBox.Width - countdownLabel.Width) / 2,
                (LiveViewPicBox.Height - countdownLabel.Height) / 2
            );

            if (countdownValue < 0)
            {
                countdownTimer.Stop();
                countdownLabel.Visible = false;

                // Take photo after countdown finishes
                try
                {
                    if ((string)TvCoBox.SelectedItem == "Bulb")
                        MainCamera.TakePhotoBulbAsync((int)BulbUpDo.Value, ""); //need to fix
                    else
                        MainCamera.TakePhotoShutterAsync();
                }
                catch (Exception ex) { ReportError(ex.Message, false); }
            }
        }

        private void RecordVideoButton_Click(object sender, EventArgs e)
        {
            try
            {
                Recording state = (Recording)MainCamera.GetInt32Setting(PropertyID.Record);
                if (state != Recording.On)
                {
                    MainCamera.StartFilming(true);
                    RecordVideoButton.Text = "Stop Video";
                }
                else
                {
                    bool save = STComputerRdButton.Checked || STBothRdButton.Checked;
                    MainCamera.StopFilming(save);
                    RecordVideoButton.Text = "Record Video";
                }
            }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        private void BrowseButton_Click(object sender, EventArgs e)
        {
            try
            {
                if (Directory.Exists(SavePathTextBox.Text)) SaveFolderBrowser.SelectedPath = SavePathTextBox.Text;
                if (SaveFolderBrowser.ShowDialog() == DialogResult.OK)
                {
                    SavePathTextBox.Text = SaveFolderBrowser.SelectedPath;
                }
            }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        private void AvCoBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                if (AvCoBox.SelectedIndex < 0) return;
                MainCamera.SetSetting(PropertyID.Av, AvValues.GetValue((string)AvCoBox.SelectedItem).IntValue);
            }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        private void TvCoBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                if (TvCoBox.SelectedIndex < 0) return;

                MainCamera.SetSetting(PropertyID.Tv, TvValues.GetValue((string)TvCoBox.SelectedItem).IntValue);
                BulbUpDo.Enabled = (string)TvCoBox.SelectedItem == "Bulb";
            }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        private void ISOCoBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                if (ISOCoBox.SelectedIndex < 0) return;
                MainCamera.SetSetting(PropertyID.ISO, ISOValues.GetValue((string)ISOCoBox.SelectedItem).IntValue);
            }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        private void SaveToRdButton_CheckedChanged(object sender, EventArgs e)
        {
            try
            {
                if (IsInit)
                {
                    if (STCameraRdButton.Checked)
                    {
                        MainCamera.SetSetting(PropertyID.SaveTo, (int)SaveTo.Camera);
                        BrowseButton.Enabled = false;
                        SavePathTextBox.Enabled = false;
                    }
                    else
                    {
                        if (STComputerRdButton.Checked) MainCamera.SetSetting(PropertyID.SaveTo, (int)SaveTo.Host);
                        else if (STBothRdButton.Checked) MainCamera.SetSetting(PropertyID.SaveTo, (int)SaveTo.Both);

                        MainCamera.SetCapacity(4096, int.MaxValue);
                        BrowseButton.Enabled = true;
                        SavePathTextBox.Enabled = true;
                    }
                }
            }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        #endregion




        #region Live view

        private void LiveViewButton_Click(object sender, EventArgs e)
        {
            try
            {
                if (!MainCamera.IsLiveViewOn) { MainCamera.StartLiveView(); LiveViewButton.Text = "Stop LV"; }
                else { MainCamera.StopLiveView(); LiveViewButton.Text = "Start LV"; }
            }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        private void LiveViewPicBox_SizeChanged(object sender, EventArgs e)
        {
            try
            {
                LVBw = LiveViewPicBox.Width;
                LVBh = LiveViewPicBox.Height;
                LiveViewPicBox.Invalidate();
            }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        private void LiveViewPicBox_Paint(object sender, PaintEventArgs e)
        {
            if (MainCamera == null || !MainCamera.SessionOpen) return;

            // Draw selected template background
            if (selectedTemplateImage != null)
                e.Graphics.DrawImage(selectedTemplateImage, 0, 0, LiveViewPicBox.Width, LiveViewPicBox.Height);

            if (lastCapturedImage != null)
            {
                float ratio = Math.Min((float)LiveViewPicBox.Width / lastCapturedImage.Width,
                                      (float)LiveViewPicBox.Height / lastCapturedImage.Height);
                int newWidth = (int)(lastCapturedImage.Width * ratio);
                int newHeight = (int)(lastCapturedImage.Height * ratio);
                e.Graphics.DrawImage(lastCapturedImage,
                                    (LiveViewPicBox.Width - newWidth) / 2,
                                    (LiveViewPicBox.Height - newHeight) / 2,
                                    newWidth, newHeight);
            }
            else if (!MainCamera.IsLiveViewOn && selectedTemplateImage == null)
                e.Graphics.Clear(BackColor);
            else if (MainCamera.IsLiveViewOn)
            {
                lock (LvLock)
                {
                    if (Evf_Bmp != null)
                    {
                        LVBratio = LVBw / (float)LVBh;
                        LVration = Evf_Bmp.Width / (float)Evf_Bmp.Height;
                        if (LVBratio < LVration)
                        {
                            w = LVBw;
                            h = (int)(LVBw / LVration);
                        }
                        else
                        {
                            w = (int)(LVBh * LVration);
                            h = LVBh;
                        }
                        e.Graphics.DrawImage(Evf_Bmp, 0, 0, w, h);
                    }
                }
            }
        }

        private void FocusNear3Button_Click(object sender, EventArgs e)
        {
            try { MainCamera.SendCommand(CameraCommand.DriveLensEvf, (int)DriveLens.Near3); }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        private void FocusNear2Button_Click(object sender, EventArgs e)
        {
            try { MainCamera.SendCommand(CameraCommand.DriveLensEvf, (int)DriveLens.Near2); }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        private void FocusNear1Button_Click(object sender, EventArgs e)
        {
            try { MainCamera.SendCommand(CameraCommand.DriveLensEvf, (int)DriveLens.Near1); }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        private void FocusFar1Button_Click(object sender, EventArgs e)
        {
            try { MainCamera.SendCommand(CameraCommand.DriveLensEvf, (int)DriveLens.Far1); }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        private void FocusFar2Button_Click(object sender, EventArgs e)
        {
            try { MainCamera.SendCommand(CameraCommand.DriveLensEvf, (int)DriveLens.Far2); }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        private void FocusFar3Button_Click(object sender, EventArgs e)
        {
            try { MainCamera.SendCommand(CameraCommand.DriveLensEvf, (int)DriveLens.Far3); }
            catch (Exception ex) { ReportError(ex.Message, false); }
        }

        #endregion




        #region Subroutines

        private void CloseSession()
        {
            MainCamera.CloseSession();
            AvCoBox.Items.Clear();
            TvCoBox.Items.Clear();
            ISOCoBox.Items.Clear();
            SettingsGroupBox.Enabled = false;
            LiveViewGroupBox.Enabled = false;
            SessionButton.Text = "Open Session";
            SessionLabel.Text = "No open session";
            LiveViewButton.Text = "Start LV";
        }

        private void button1_Click(object sender, EventArgs e)
        {
            if (lastCapturedImage == null)
            {
                MessageBox.Show("No image captured to send.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var dialog = new Form
            {
                Text = "Send Email",
                Size = new System.Drawing.Size(300, 150),
                StartPosition = FormStartPosition.CenterParent
            })
            {
                var emailBox = new TextBox { Width = 250, Top = 20, Left = 20 };
                var sendButton = new Button { Text = "Send", Top = 60, Left = 20 };

                sendButton.Click += (s, args) =>
                {
                    if (IsValidEmail(emailBox.Text))
                    {
                        SendEmail(emailBox.Text, lastCapturedImage);
                        dialog.Close();
                    }
                    else
                    {
                        MessageBox.Show("Invalid email address!");
                    }
                };

                dialog.Controls.Add(emailBox);
                dialog.Controls.Add(sendButton);
                dialog.ShowDialog();
            }
        }

        private void button1_Click_1(object sender, EventArgs e)
        {
            if (lastCapturedImage == null)
            {
                MessageBox.Show("No image to print.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            printDocument = new PrintDocument();
            printDocument.PrintPage += (s, ev) =>
            {
                // Draw the image to fill the entire page, stretching if necessary
                ev.Graphics.DrawImage(lastCapturedImage, ev.PageBounds);
            };

            using (PrintDialog printDialog = new PrintDialog { Document = printDocument })
            {
                if (printDialog.ShowDialog() == DialogResult.OK)
                {
                    printDocument.Print();
                }
            }
        }

        private void RefreshCamera()
        {
            CameraListBox.Items.Clear();
            CamList = APIHandler.GetCameraList();
            foreach (Camera cam in CamList) CameraListBox.Items.Add(cam.DeviceName);
            if (MainCamera?.SessionOpen == true) CameraListBox.SelectedIndex = CamList.FindIndex(t => t.ID == MainCamera.ID);
            else if (CamList.Count > 0) CameraListBox.SelectedIndex = 0;
        }

        private void OpenSession()
        {
            if (CameraListBox.SelectedIndex >= 0)
            {
                MainCamera = CamList[CameraListBox.SelectedIndex];
                MainCamera.OpenSession();
                MainCamera.LiveViewUpdated += MainCamera_LiveViewUpdated;
                MainCamera.ProgressChanged += MainCamera_ProgressChanged;
                MainCamera.StateChanged += MainCamera_StateChanged;
                MainCamera.DownloadReady += MainCamera_DownloadReady;

                SessionButton.Text = "Close Session";
                SessionLabel.Text = MainCamera.DeviceName;
                AvList = MainCamera.GetSettingsList(PropertyID.Av);
                TvList = MainCamera.GetSettingsList(PropertyID.Tv);
                ISOList = MainCamera.GetSettingsList(PropertyID.ISO);
                foreach (var Av in AvList) AvCoBox.Items.Add(Av.StringValue);
                foreach (var Tv in TvList) TvCoBox.Items.Add(Tv.StringValue);
                foreach (var ISO in ISOList) ISOCoBox.Items.Add(ISO.StringValue);
                AvCoBox.SelectedIndex = AvCoBox.Items.IndexOf(AvValues.GetValue(MainCamera.GetInt32Setting(PropertyID.Av)).StringValue);
                TvCoBox.SelectedIndex = TvCoBox.Items.IndexOf(TvValues.GetValue(MainCamera.GetInt32Setting(PropertyID.Tv)).StringValue);
                ISOCoBox.SelectedIndex = ISOCoBox.Items.IndexOf(ISOValues.GetValue(MainCamera.GetInt32Setting(PropertyID.ISO)).StringValue);
                SettingsGroupBox.Enabled = true;
                LiveViewGroupBox.Enabled = true;
            }
        }

        private void ReportError(string message, bool lockdown)
        {
            int errc;
            lock (ErrLock) { errc = ++ErrCount; }

            if (lockdown) EnableUI(false);

            if (errc < 4) MessageBox.Show(message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else if (errc == 4) MessageBox.Show("Many errors happened!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

            lock (ErrLock) { ErrCount--; }
        }

        private void EnableUI(bool enable)
        {
            if (InvokeRequired) Invoke((Action)delegate { EnableUI(enable); });
            else
            {
                SettingsGroupBox.Enabled = enable;
                InitGroupBox.Enabled = enable;
                LiveViewGroupBox.Enabled = enable;
            }
        }

        #endregion        
    }
}
