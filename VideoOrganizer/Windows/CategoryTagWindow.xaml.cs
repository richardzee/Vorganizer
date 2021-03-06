﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
using System.Windows.Shapes;
using VideoOrganizer.Model;
using VideoOrganizer.Service;

namespace VideoOrganizer.Windows
{
    /// <summary>
    /// Interaction logic for CategoryWindow.xaml
    /// </summary>
    public partial class CategoryTagWindow : Window
    {
        private List<CategoryModel> categories;
        DatabaseService dbService;
        private List<VideoModel> videos;

        public CategoryTagWindow()
        {
            InitializeComponent();

            dbService = DatabaseService.Instance;
            UpdateCategories();
        }

        public CategoryTagWindow(VideoModel currVideo) : this()
        {
            videos = new List<VideoModel> { currVideo };
        }

        public CategoryTagWindow(IList videos) : this()
        {
            this.videos =  videos.Cast<VideoModel>().ToList();
        }

        private void UpdateCategories()
        {
            categories = dbService.FindAllCategories();
            cbCategory.ItemsSource = categories;
        }

        private void btnAddCategory_Click(object sender, RoutedEventArgs e)
        {
            CategoryModel category = dbService.AddCategory(tbCategory.Text);
            UpdateCategories();
            tbCategory.Text = "";
        }

        private void btnAddTag_Click(object sender, RoutedEventArgs e)
        {
            if (rbAddNewTag.IsChecked.Value)
            {
                Debug.Write("Adding new tag and then adding tag to video");
                dbService.AddTag(tbAddTag.Text, cbCategory.Text);
                videos.ForEach(video => dbService.AddTagToVideo(video, dbService.FindTagByName(tbAddTag.Text)));
            }
            else
            {
                Debug.Write("Adding existing tag to video");
                videos.ForEach(video => dbService.AddTagToVideo(video, (TagModel)cbTag.SelectedItem));
            }
            
            this.Close();
        }

        private void cbCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            //Debug.WriteLine(((CategoryModel)cbCategory.SelectedItem).Name);
            List<TagModel> tags =dbService.FindTagsByCategory((CategoryModel)cbCategory.SelectedItem);
            cbTag.ItemsSource = tags;
        }
        

        private void Tag_Checked(object sender, RoutedEventArgs e)
        {
            //TODO: REFACTOR
            if (sender is RadioButton)
            {
                RadioButton selectedButton = (RadioButton)sender;
                if (selectedButton.Name.Equals("rbSelectTag"))
                {
                    cbTag.IsEnabled = true;
                    tbAddTag.IsEnabled = false;
                }
                else
                {
                    cbTag.IsEnabled = false;
                    tbAddTag.IsEnabled = true;
                    tbAddTag.Text = tbAddTag.Text.Equals("Add new tag") ? "" : tbAddTag.Text;
                }
            }
            else if (sender is StackPanel)
            {
                if (((StackPanel)sender).Name.Equals("spSelectTag"))
                {
                    cbTag.IsEnabled = true;
                    tbAddTag.IsEnabled = false;

                    rbSelectTag.IsChecked = true;
                }
                else
                {
                    cbTag.IsEnabled = false;
                    tbAddTag.IsEnabled = true;
                    tbAddTag.Text = tbAddTag.Text.Equals("Add new tag") ? "" : tbAddTag.Text;

                    rbAddNewTag.IsChecked = true;
                }
            }
        }

        private void tbAddTag_KeyUp(object sender, KeyEventArgs e)
        {
            if(e.Key == Key.Enter)
            {
                btnAddTag_Click(sender, e);
            }

        }
    }
}
