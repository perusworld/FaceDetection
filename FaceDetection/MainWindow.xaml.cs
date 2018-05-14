using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Microsoft.Win32;
using System.IO;
using System.Threading;
using Microsoft.ProjectOxford.Common.Contract;
using Microsoft.ProjectOxford.Face;
using Microsoft.ProjectOxford.Face.Contract;
using System.Net;
using System.Globalization;

namespace FaceDetection
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        private readonly IFaceServiceClient faceServiceClient =
          new FaceServiceClient("--your-subscription-id--", "https://westus.api.cognitive.microsoft.com/face/v1.0");

        const int CallLimitPerSecond = 10;
        static Queue<DateTime> _timeStampQueue = new Queue<DateTime>(CallLimitPerSecond);

        Person[] prsns;
        Face[] faces;
        String[] faceDescriptions;
        double resizeFactor;


        public MainWindow()
        {
            InitializeComponent();
        }

        static async Task WaitCallLimitPerSecondAsync()
        {
            Monitor.Enter(_timeStampQueue);
            try
            {
                if (_timeStampQueue.Count >= CallLimitPerSecond)
                {
                    TimeSpan timeInterval = DateTime.UtcNow - _timeStampQueue.Peek();
                    if (timeInterval < TimeSpan.FromSeconds(1))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1) - timeInterval);
                    }
                    _timeStampQueue.Dequeue();
                }
                _timeStampQueue.Enqueue(DateTime.UtcNow);
            }
            finally
            {
                Monitor.Exit(_timeStampQueue);
            }
        }

        private void btnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Forms.FolderBrowserDialog fbd = new System.Windows.Forms.FolderBrowserDialog();
            System.Windows.Forms.DialogResult result = fbd.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK)
            {
                txtImgFolder.Text = fbd.SelectedPath;
            }
        }

        private async Task loadPersons(string id)
        {
            lstPersons.Items.Clear();
            try
            {
                prsns = await faceServiceClient.ListPersonsInPersonGroupAsync(id);
                foreach (Person prsn in prsns)
                {
                    lstPersons.Items.Add(prsn.Name);
                }
            }
            catch (FaceAPIException ex)
            {
            }
        }

        private void ShowErrorMsg(string msg)
        {
            MessageBox.Show(msg, "Error");
        }

        private async Task deletePerson(string id)
        {
            if (-1 < lstPersons.SelectedIndex)
            {
                try
                {
                    await faceServiceClient.DeletePersonFromPersonGroupAsync(id, prsns[lstPersons.SelectedIndex].PersonId);
                    await WaitCallLimitPerSecondAsync();
                    await loadPersons(id);
                }
                catch (FaceAPIException ex)
                {
                    ShowErrorMsg(ex.Message);
                }
            }
        }

        private async Task ensurePersonGroup(string id, string name)
        {
            try
            {
                PersonGroup grp = await faceServiceClient.GetPersonGroupAsync(id);
            }
            catch (FaceAPIException ex)
            {
                if (HttpStatusCode.NotFound == ex.HttpStatus)
                {
                    await WaitCallLimitPerSecondAsync();
                    await faceServiceClient.CreatePersonGroupAsync(id, name);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private async Task beginTraining(string id)
        {
            faceServiceClient.TrainPersonGroupAsync(txtPersonGroupId.Text);
            await WaitCallLimitPerSecondAsync();
            TrainingStatus trainingStatus = null;
            while (true)
            {
                trainingStatus = await faceServiceClient.GetPersonGroupTrainingStatusAsync(id);
                Console.WriteLine(trainingStatus.Status);
                if (trainingStatus.Status != Status.Running)
                {
                    break;
                }
                await Task.Delay(1000);
            }
            ShowErrorMsg("Done");
        }

        private async Task startTraining()
        {
            await ensurePersonGroup(txtPersonGroupId.Text, txtPersonGroupName.Text);
            if (-1 == lstPersons.SelectedIndex)
            {
                await WaitCallLimitPerSecondAsync();
                if ("" == txtPersonName.Text)
                {
                    ShowErrorMsg("Select from the list or add a new person name");
                }
                else
                {
                    CreatePersonResult personResult = await faceServiceClient.CreatePersonInPersonGroupAsync(txtPersonGroupId.Text, txtPersonName.Text);
                    MessageBox.Show("Please load persons list and select from there and upload.", "Person Added");
                }
            }
            else
            {
                await uploadImages(txtPersonGroupId.Text, prsns[lstPersons.SelectedIndex].PersonId, txtImgFolder.Text);
                beginTraining(txtPersonGroupId.Text);
            }

        }

        private async Task uploadImages(string grpId, Guid prsnId, string path)
        {
            try
            {
                foreach (string imagePath in Directory.GetFiles(path, "*.jpg"))
                {
                    await WaitCallLimitPerSecondAsync();

                    using (Stream stream = File.OpenRead(imagePath))
                    {
                        await faceServiceClient.AddPersonFaceInPersonGroupAsync(grpId, prsnId, stream);
                    }
                }
            }
            catch (Exception ex)
            {
                ShowErrorMsg(ex.Message);
            }
        }

        private void btnTrain_Click(object sender, RoutedEventArgs e)
        {
            startTraining();
        }

        private void btnLoadPersons_Click(object sender, RoutedEventArgs e)
        {
            loadPersons(txtPersonGroupId.Text);
        }

        private void btnDeletePerson_Click(object sender, RoutedEventArgs e)
        {
            deletePerson(txtPersonGroupId.Text);
        }

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var openDlg = new Microsoft.Win32.OpenFileDialog();
            openDlg.Filter = "JPEG Image(*.jpg)|*.jpg";
            bool? result = openDlg.ShowDialog(this);
            if (!(bool)result)
            {
                return;
            }
            string filePath = openDlg.FileName;
            Uri fileUri = new Uri(filePath);
            BitmapImage bitmapSource = new BitmapImage();
            bitmapSource.BeginInit();
            bitmapSource.CacheOption = BitmapCacheOption.None;
            bitmapSource.UriSource = fileUri;
            bitmapSource.EndInit();
            FacePhoto.Source = bitmapSource;

            Title = "Detecting...";
            faces = await UploadAndDetectFaces(filePath);
            Title = String.Format("Detection Finished. {0} face(s) detected", faces.Length);
            var faceIds = faces.Select(face => face.FaceId).ToArray();
            var results = await faceServiceClient.IdentifyAsync(faceIds, txtPersonGroupId.Text);
            List<string> identities = new List<string>();
            foreach (var identifyResult in results)
            {
                Console.WriteLine("Result of face: {0}", identifyResult.FaceId);
                if (identifyResult.Candidates.Length == 0)
                {
                    identities.Add("Unknown");
                    Console.WriteLine("No one identified");
                }
                else
                {
                    var candidateId = identifyResult.Candidates[0].PersonId;
                    var person = await faceServiceClient.GetPersonAsync(txtPersonGroupId.Text, candidateId);
                    identities.Add(person.Name);
                    Console.WriteLine("Identified as {0}", person.Name);
                }
            }

            if (faces.Length > 0)
            {
                DrawingVisual visual = new DrawingVisual();
                DrawingContext drawingContext = visual.RenderOpen();
                drawingContext.DrawImage(bitmapSource,
                    new Rect(0, 0, bitmapSource.Width, bitmapSource.Height));
                double dpi = bitmapSource.DpiX;
                resizeFactor = 96 / dpi;
                faceDescriptions = new String[faces.Length];

                for (int i = 0; i < faces.Length; ++i)
                {
                    Face face = faces[i];

                    FormattedText formattedText = new FormattedText(identities[i], CultureInfo.GetCultureInfo("en-us"), FlowDirection.LeftToRight, new Typeface("Verdana"),32,Brushes.Yellow);
                    drawingContext.DrawText(formattedText, new Point(face.FaceRectangle.Left * resizeFactor, (face.FaceRectangle.Top * resizeFactor) - 32));
                    // Draw a rectangle on the face.
                    drawingContext.DrawRectangle(
                        Brushes.Transparent,
                        new Pen(Brushes.Red, 2),
                        new Rect(
                            face.FaceRectangle.Left * resizeFactor,
                            face.FaceRectangle.Top * resizeFactor,
                            face.FaceRectangle.Width * resizeFactor,
                            face.FaceRectangle.Height * resizeFactor
                            )
                    );

                    // Store the face description.
                    faceDescriptions[i] = FaceDescription(face);
                }

                drawingContext.Close();

                // Display the image with the rectangle around the face.
                RenderTargetBitmap faceWithRectBitmap = new RenderTargetBitmap(
                    (int)(bitmapSource.PixelWidth * resizeFactor),
                    (int)(bitmapSource.PixelHeight * resizeFactor),
                    96,
                    96,
                    PixelFormats.Pbgra32);

                faceWithRectBitmap.Render(visual);
                FacePhoto.Source = faceWithRectBitmap;

                // Set the status bar text.
                faceDescriptionStatusBar.Text = "Place the mouse pointer over a face to see the face description.";
            }

        }

        private async Task<Face[]> UploadAndDetectFaces(string imageFilePath)
        {
            IEnumerable<FaceAttributeType> faceAttributes =
                new FaceAttributeType[] { FaceAttributeType.Gender, FaceAttributeType.Age, FaceAttributeType.Smile, FaceAttributeType.Emotion, FaceAttributeType.Glasses, FaceAttributeType.Hair };

            try
            {
                using (Stream imageFileStream = File.OpenRead(imageFilePath))
                {
                    Face[] faces = await faceServiceClient.DetectAsync(imageFileStream, returnFaceId: true, returnFaceLandmarks: false, returnFaceAttributes: faceAttributes);
                    return faces;
                }
            }
            catch (FaceAPIException f)
            {
                MessageBox.Show(f.ErrorMessage, f.ErrorCode);
                return new Face[0];
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "Error");
                return new Face[0];
            }
        }


        private string FaceDescription(Face face)
        {
            StringBuilder sb = new StringBuilder();

            sb.Append("Face: ");

            // Add the gender, age, and smile.
            sb.Append(face.FaceAttributes.Gender);
            sb.Append(", ");
            sb.Append(face.FaceAttributes.Age);
            sb.Append(", ");
            sb.Append(String.Format("smile {0:F1}%, ", face.FaceAttributes.Smile * 100));

            // Add the emotions. Display all emotions over 10%.
            sb.Append("Emotion: ");
            EmotionScores emotionScores = face.FaceAttributes.Emotion;
            if (emotionScores.Anger >= 0.1f) sb.Append(String.Format("anger {0:F1}%, ", emotionScores.Anger * 100));
            if (emotionScores.Contempt >= 0.1f) sb.Append(String.Format("contempt {0:F1}%, ", emotionScores.Contempt * 100));
            if (emotionScores.Disgust >= 0.1f) sb.Append(String.Format("disgust {0:F1}%, ", emotionScores.Disgust * 100));
            if (emotionScores.Fear >= 0.1f) sb.Append(String.Format("fear {0:F1}%, ", emotionScores.Fear * 100));
            if (emotionScores.Happiness >= 0.1f) sb.Append(String.Format("happiness {0:F1}%, ", emotionScores.Happiness * 100));
            if (emotionScores.Neutral >= 0.1f) sb.Append(String.Format("neutral {0:F1}%, ", emotionScores.Neutral * 100));
            if (emotionScores.Sadness >= 0.1f) sb.Append(String.Format("sadness {0:F1}%, ", emotionScores.Sadness * 100));
            if (emotionScores.Surprise >= 0.1f) sb.Append(String.Format("surprise {0:F1}%, ", emotionScores.Surprise * 100));

            // Add glasses.
            sb.Append(face.FaceAttributes.Glasses);
            sb.Append(", ");

            // Add hair.
            sb.Append("Hair: ");

            // Display baldness confidence if over 1%.
            if (face.FaceAttributes.Hair.Bald >= 0.01f)
                sb.Append(String.Format("bald {0:F1}% ", face.FaceAttributes.Hair.Bald * 100));

            // Display all hair color attributes over 10%.
            HairColor[] hairColors = face.FaceAttributes.Hair.HairColor;
            foreach (HairColor hairColor in hairColors)
            {
                if (hairColor.Confidence >= 0.1f)
                {
                    sb.Append(hairColor.Color.ToString());
                    sb.Append(String.Format(" {0:F1}% ", hairColor.Confidence * 100));
                }
            }

            // Return the built string.
            return sb.ToString();
        }

        private void FacePhoto_MouseMove(object sender, MouseEventArgs e)
        {
            // If the REST call has not completed, return from this method.
            if (faces == null)
                return;

            // Find the mouse position relative to the image.
            Point mouseXY = e.GetPosition(FacePhoto);

            ImageSource imageSource = FacePhoto.Source;
            BitmapSource bitmapSource = (BitmapSource)imageSource;

            // Scale adjustment between the actual size and displayed size.
            var scale = FacePhoto.ActualWidth / (bitmapSource.PixelWidth / resizeFactor);

            // Check if this mouse position is over a face rectangle.
            bool mouseOverFace = false;

            for (int i = 0; i < faces.Length; ++i)
            {
                FaceRectangle fr = faces[i].FaceRectangle;
                double left = fr.Left * scale;
                double top = fr.Top * scale;
                double width = fr.Width * scale;
                double height = fr.Height * scale;

                // Display the face description for this face if the mouse is over this face rectangle.
                if (mouseXY.X >= left && mouseXY.X <= left + width && mouseXY.Y >= top && mouseXY.Y <= top + height)
                {
                    faceDescriptionStatusBar.Text = faceDescriptions[i];
                    mouseOverFace = true;
                    break;
                }
            }

            // If the mouse is not over a face rectangle.
            if (!mouseOverFace)
                faceDescriptionStatusBar.Text = "Place the mouse pointer over a face to see the face description.";

        }
    }
}
