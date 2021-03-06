﻿using MediaToolkit;
using MediaToolkit.Model;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoOrganizer.Model;
using VideoOrganizer.Service;
using VideoOrganizer.Windows;

using System.Runtime.Caching;

namespace VideoOrganizer
{
    /// <summary>
    /// Interaction logic for MainPage.xaml
    /// </summary>
    public partial class MainPage : Page
    {
        private DatabaseService dbService;
        private LogService Logger;
        private VideoModel currVideo;
        private Dictionary<long, CategoryModel> categoryModelsMap;
        private NReco.VideoConverter.FFMpegConverter ffMpeg;
        private OpenFileDialog openDialog;
        private ObjectCache cache;


        public MainPage()
        {
            InitializeComponent();
            this.DataContext = this;
            dbService = DatabaseService.Instance;
            currVideo = null;
            if (lvOrganize.ItemsSource == null)
            {
                lvOrganize.ItemsSource = dbService.FindAllVideos();
            }

            Logger = new LogService();
            LogTextBlock.DataContext = Logger;
            ThreadPool.SetMinThreads(100, 100);

            openDialog = new OpenFileDialog();
            openDialog.Multiselect = true;
            cbCategorySearch.ItemsSource = dbService.FindAllCategories();

            // ------caching------

            cache = MemoryCache.Default;
            string fileContents = cache["filecontents"] as string;
            if(fileContents == null)
            {
                CacheItemPolicy policy = new CacheItemPolicy();
                string cachePath = Path.GetTempPath() + "vorgCache";
                if (!File.Exists(cachePath))
                {
                    File.Create(cachePath).Close();
                }
                List<string> filePaths = new List<string>();
                filePaths.Add(cachePath);
                policy.ChangeMonitors.Add(new HostFileChangeMonitor(filePaths));
                fileContents = File.ReadAllText(cachePath) + "\n" + DateTime.Now;
                cache.Set("filecontents", fileContents, policy);
            }

            ffMpeg = new NReco.VideoConverter.FFMpegConverter();
        }
        
        private void Grid_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                //gets the path of drag and drop file
                //TODO: handle directory drag and drop

                //iterate through each file in directory
                String[] files = (string[])e.Data.GetData(DataFormats.FileDrop);

                ParseAndImportFiles(files);
            }
        }

        /// <summary>
        /// Parses string of paths and then imports them. Sends to logger when complete
        /// </summary>
        /// <param name="files"></param>
        private async void ParseAndImportFiles(String[] files)
        {
            Logger.Log(string.Format("Beginning Import..."));

            await Task<Tuple<int, int>>.Factory.StartNew(() =>
            {
                int validFiles = 0, invalidFiles = 0;
                foreach (String i in files)
                {
                    Tuple<int, int> result = ImportFiles(i);
                    validFiles += result.Item1;
                    invalidFiles += result.Item2;
                }
                return Tuple.Create<int, int>(validFiles, invalidFiles);
            }).ContinueWith((result) =>
            {
                Logger.Log(string.Format("Import complete!"));
                Logger.Log(string.Format("Imported {0} files", result.Result.Item1));
                if (result.Result.Item2 != 0)
                    Logger.Log(string.Format("Failed to import {0} files", result.Result.Item2));
                Dispatcher.Invoke(() => lvOrganize.ItemsSource = dbService.FindAllVideos());
            });
            scrollViewLog.ScrollToBottom();
        }

        /// <summary>
        /// Analyzes and imports files from the paths provided. 
        /// </summary>
        /// <param name="path">String array of paths</param>
        /// <returns>Tuple of valid files imported and invalid files</returns>
        private Tuple<int, int> ImportFiles(string path)
        {
            //i've been playing around with tuples in this class lol
            int validFiles = 0, invalidFiles = 0;
            var fileName = path.Substring(path.LastIndexOf('\\') + 1);
            FileAttributes attr = File.GetAttributes(@path);
            FileInfo fileInfo = new FileInfo(path);
            if (attr.HasFlag(FileAttributes.Directory))
            {
                //handles directories and files within directory
                List<string> directories = Directory.GetDirectories(path).ToList();
                List<string> files = Directory.GetFiles(path).ToList();
                List<Task<Tuple<int,int>>> tasks = new List<Task<Tuple<int, int>>>();

                //Logger.Log(directories.Aggregate("", (acc, x) => acc += " \n" + x));

                Task<Tuple<int,int>> t1 = Task<Tuple<int, int>>.Factory.StartNew(() =>
                {
                    int TaskValidFiles=0, TaskInvalidFiles = 0;
                    files.ForEach(file =>
                    {
                        Tuple<int, int> result = ImportFiles(file);
                        TaskValidFiles += result.Item1;
                        TaskInvalidFiles += result.Item2;

                    });

                     return Tuple.Create<int, int>(TaskValidFiles,TaskInvalidFiles);
                }, TaskCreationOptions.AttachedToParent);
                tasks.Add(t1);
                
                directories.ForEach(directory =>
                {
                    Task<Tuple<int,int>> t2 = Task<Tuple<int,int>>.Factory.StartNew(() =>
                    {
                        int TaskValidFiles = 0, TaskInvalidFiles = 0;
                        Logger.Log(string.Format("Found directory {0}", directory));
                        Tuple<int, int> result = ImportFiles(directory);
                        TaskValidFiles += result.Item1;
                        TaskInvalidFiles += result.Item2;

                        return Tuple.Create<int, int>(TaskValidFiles, TaskInvalidFiles);
                    }, TaskCreationOptions.AttachedToParent);
                    tasks.Add(t2);
                });

                Task.WaitAll(tasks.ToArray());
                
                tasks.ForEach(task =>
                {
                    validFiles += task.Result.Item1;
                    invalidFiles += task.Result.Item2;
                });
                
            }
            else
            {
                //handles single file
                var inputFile = new MediaFile { Filename = @path };
                using (var engine = new Engine())
                {
                    engine.GetMetadata(inputFile);

                    if (inputFile.Metadata == null ||
                        inputFile.Metadata.AudioData == null || inputFile.Metadata.VideoData == null|| 
                        inputFile.Metadata.Duration.Equals(TimeSpan.Zero))
                    {
                        invalidFiles++;
                        Task.Run(() => Logger.LogAsync(string.Format("Failed to import {0}", fileName)));
                        return Tuple.Create<int, int>(validFiles, invalidFiles);
                    }
                    validFiles++;
                }
                long fileSize = new FileInfo(path).Length;
                long fileFps = Convert.ToInt64(inputFile.Metadata.VideoData.Fps);
                string fileResolution = inputFile.Metadata.VideoData.FrameSize;
                double fileDuration = inputFile.Metadata.Duration.TotalSeconds;
                DateTime dateOriginal = fileInfo.LastWriteTime;

                //checks if there is a duplicate found, if found, do nothing, else add it
                if ((lvOrganize.ItemsSource as List<VideoModel>).FirstOrDefault(x => x.Path.Equals(path)) == null)
                {
                    dbService.AddVideo(fileName, path, fileSize.ToString(), fileResolution,
                            fileFps, fileDuration.ToString(), "", dateOriginal, new DateTime());
                }
                else
                {
                    invalidFiles++;
                    validFiles--;
                }
                //lvOrganize.ItemsSource = dbService.FindAllVideos();
            }

            return Tuple.Create<int, int>(validFiles, invalidFiles);
        }

        /// <summary>
        /// Searches videos by calling the database service
        /// </summary>
        private void SearchVideos()
        {
            string searchText = tbSearch.Text;
            lvOrganize.ItemsSource = dbService.FindVideos(searchText);
        }

        private void EditVideo()
        {
            //TODO: implement editing a video properties (category and tag)
            throw new NotImplementedException();
        }

        /// <summary>
        /// Gets all tags for video from database service
        /// </summary>
        /// <returns></returns>
        private List<TagModel> GetTagsForVideo()
        {
            return dbService.FindVideoTags(currVideo.Id);
        }

        /// <summary>
        /// Sets up the edit page by looking at current video selected. Does nothing if current video is null
        /// </summary>
        private void SetupEditPage()
        {
            //sets all edit page information
            if(currVideo == null) return;
            lbEditName.DataContext = currVideo;
            lbEditFilePath.DataContext = currVideo;
            lbEditDateAdded.DataContext = currVideo;
            lbEditDateLastWatched.DataContext = currVideo;
            lbEditDuration.DataContext = currVideo;
            lbEditFavorite.DataContext = currVideo;
            lbEditFileSize.DataContext = currVideo;
            lbEditFps.DataContext = currVideo;
            lbEditPlayCount.DataContext = currVideo;
            lbEditRating.DataContext = currVideo;
            lbEditResolution.DataContext = currVideo;
            lbEditOriginalDate.DataContext = currVideo;

            /*
            var MemStream = new MemoryStream();
            var thumbNailSource = new BitmapImage();
            ffMpeg.GetVideoThumbnail(currVideo.Path, "C:/temp/thumbnail.jpeg", 60f);
            MemStream.Position = 0;
            thumbNailSource.BeginInit();
            thumbNailSource.StreamSource = MemStream;
            thumbNailSource.EndInit();
            videoThumbnail.Source = thumbNailSource;
            */

            //creates thumbnail
            try
            {
                if (cache[currVideo.Name] == null)
                {
                    var thumbNailSource = new BitmapImage();
                    using (var memStream = new MemoryStream())
                    {
                        ffMpeg.GetVideoThumbnail(currVideo.Path, memStream, 60f);
                        memStream.Position = 0;
                        thumbNailSource.BeginInit();
                        thumbNailSource.CacheOption = BitmapCacheOption.OnLoad;
                        thumbNailSource.StreamSource = memStream;
                        thumbNailSource.EndInit();
                        thumbNailSource.Freeze();
                        cache[currVideo.Name] = thumbNailSource;
                        videoThumbnail.Source = thumbNailSource;
                    }
                }
                else
                {
                    videoThumbnail.Source = cache[currVideo.Name] as ImageSource;
                }
            }
            catch (System.NotSupportedException)
            {
                Logger.Log("Couldn't create screenshot");
                videoThumbnail.Source = null;
            }

            //opens up side panel
            ChildGrid.ColumnDefinitions[2].Width = new GridLength(this.WindowWidth / 3);

            UpdateVideoTags(currVideo);
        }

        /// <summary>
        /// Updates the video tags on the edit page for the selected video
        /// </summary>
        /// <param name="video"></param>
        /// <returns></returns>
        public Dictionary<CategoryModel, List<TagModel>> UpdateVideoTags(VideoModel video)
        {
            List<UIElement> stackPanelCollection = new List<UIElement>();
            foreach(UIElement child in stackPanelTags.Children){
                if (child.Uid.Contains("taggedChildren"))
                {
                    stackPanelCollection.Add(child);
                }
            }
            stackPanelCollection.ForEach(uiElement => stackPanelTags.Children.Remove(uiElement));
            Dictionary<CategoryModel, List<TagModel>> videoTags = new Dictionary<CategoryModel, List<TagModel>>();
            List<TagModel> allTags = dbService.FindVideoTags(video);
            categoryModelsMap = allTags.Select(tags => tags.Category)
                                                                        .GroupBy(cat => cat.Id)
                                                                        .Select(cat => cat.First())
                                                                        .ToDictionary(cat => cat.Id);

            videoTags = allTags.GroupBy(tags => tags.Category.Id)
                                .ToDictionary(group => categoryModelsMap[group.Key], group => group.ToList());

            //creates UI elements for category and tags
            categoryModelsMap.Values.ToList().ForEach(category => {
                //creates label
                Label lblCat = new Label();
                lblCat.Uid = "taggedChildrenLbl_" + category.Id;
                lblCat.Content = category.Name;
                stackPanelTags.Children.Add(lblCat);

                //adds listbox of Tags
                ListBox listBoxTags = new ListBox();
                listBoxTags.Uid = "taggedChildrenListBox_" + category.Name;
                listBoxTags.MouseDoubleClick += new MouseButtonEventHandler(element_TagDoubleClick);
                listBoxTags.KeyUp += new KeyEventHandler(element_TagKeyUp);

                //create datatemplate

                var textBlockFactory = new FrameworkElementFactory(typeof(TextBlock));
                textBlockFactory.SetValue(TextBlock.TextProperty, new Binding(". Tag"));
                var template = new DataTemplate();
                template.VisualTree = textBlockFactory;
                listBoxTags.ItemTemplate = template;

                //MenuItem mItem = new MenuItem();
                //mItem.Header = "Delete Tag";
                //mItem.Click += menuItemOrg_DeleteTag;
                //listBoxTags.ContextMenu = new ContextMenu();
                //listBoxTags.ContextMenu.Items.Add(mItem);
                videoTags[category].ForEach(tag => {
                    listBoxTags.Items.Add(tag);
                });
                stackPanelTags.Children.Add(listBoxTags);
            });
            
            return videoTags;
        }

        private void element_TagKeyUp(object sender, KeyEventArgs e)
        {
            if(e.Key == Key.Delete)
            {
                menuItemOrg_DeleteTag(sender, e);
            }
        }

        private void element_TagDoubleClick(object sender, MouseButtonEventArgs e)
        {
            //can get from listBox_categoryName
            throw new NotImplementedException();
        }

        private void btnSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchVideos();
        }

        private void tbSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if(e.Key == Key.Enter)
            {
                SearchVideos();
            }
        }

        private void lvOrganize_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            //Doubleclick functionality on list view opens edit window
            if (sender == null) return;
            ListView listView = sender as ListView;
            VideoModel selected = listView.SelectedItem as VideoModel;

            currVideo = selected;
            SetupEditPage();
        }

        private void btnEdit_Click(object sender, RoutedEventArgs e)
        {
            //TODO: Edit button click
            if (lvOrganize.SelectedItem != null)
            {
                currVideo = lvOrganize.SelectedItem as VideoModel;
                SetupEditPage();
            }

        }

        private void btnCloseEdit_Click(object sender, RoutedEventArgs e)
        {
            ChildGrid.ColumnDefinitions[2].Width = new GridLength(0);
        }

        private void btnSaveEdit_Click(object sender, RoutedEventArgs e)
        {
            dbService.UpdateVideo(currVideo);
        }

        /// <summary>
        /// Button Event that adds tag
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnAddTag_Click(object sender, RoutedEventArgs e)
        {
            CategoryTagWindow window = new CategoryTagWindow(currVideo)
            {
                Owner = Application.Current.MainWindow
            };
            
            window.ShowDialog();
            UpdateVideoTags(currVideo);
        }

        /// <summary>
        /// Right click event that adds the tag to the videos
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void menuItemOrg_AddTag(object sender, RoutedEventArgs e)
        {
            //TODO REFACTOR WITH btnAddTag_Click
            CategoryTagWindow window = new CategoryTagWindow(lvOrganize.SelectedItems)
            {
                Owner = Application.Current.MainWindow
            };

            window.ShowDialog();
            UpdateVideoTags(currVideo);
        }

        /// <summary>
        /// Event that deletes the file from the database
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void menuItemOrg_DeleteVideo(object sender, RoutedEventArgs e)
        {
            Logger.Log(string.Format("Deleting..."));
            foreach (VideoModel video in lvOrganize.SelectedItems)
            {
                dbService.DeleteVideo(video);
            }

            lvOrganize.ItemsSource = dbService.FindAllVideos();
            Logger.Log(string.Format("Done!"));
        }

        private void menuItemOrg_DeleteTag(object sender, RoutedEventArgs e)
        {
            Logger.Log(string.Format("Deleting..."));
            dbService.DeleteTagFromVideo(currVideo, ((sender as ListBox).SelectedItem as TagModel));
            UpdateVideoTags(currVideo);
            Logger.Log(string.Format("Done!"));
        }

        /// <summary>
        /// Key Event for organization list view 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void lvOrganize_KeyUp(object sender, KeyEventArgs e)
        {
            if(e.Key.Equals(Key.Delete)){
                menuItemOrg_DeleteVideo(sender, e);
                
            }
        }

        /// <summary>
        /// Button event to add files. Opens a dialog and then parses the data.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnAdd_Click(object sender, RoutedEventArgs e)
        {
            if(openDialog == null)
            {
                openDialog = new OpenFileDialog();
            }

            openDialog.ShowDialog();

            String[] files = openDialog.FileNames;
            ParseAndImportFiles(files);
        }

        /// <summary>
        /// Button to watch video selected
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnWatch_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(currVideo.Path);
            }catch(Win32Exception)
            {
                Logger.Log("Cannot run file. Path invalid.");
            }
        }

        private void lvOrganize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            lvOrganize_MouseDoubleClick(sender, null);
        }

        private void cbCategorySearch_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbCategorySearch.SelectedIndex != -1)
            {
                List<TagModel> tags = dbService.FindTagsByCategory((CategoryModel)cbCategorySearch.SelectedItem);
                cbTagSearch.ItemsSource = tags;
            }
        }

        private void cbTagSearch_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if(cbTagSearch.SelectedIndex != -1)
                lvOrganize.ItemsSource = dbService.FindVideosByVideoTag((TagModel)cbTagSearch.SelectedItem);
        }

        private void btnReset_Click(object sender, RoutedEventArgs e)
        {
            tbSearch.Text = "";
            cbCategorySearch.SelectedIndex = -1;
            cbTagSearch.SelectedIndex = -1;

            SearchVideos();
        }
    }
}
