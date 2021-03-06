﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.OCR;
using Newtonsoft.Json;
using WeChat.AutoJump.Domain;
using WeChat.AutoJump.IService;
using WeChat.AutoJump.Utility;

namespace WeChat.AutoJump.CMDApp
{
    class Program
    {
        static void Main(string[] args)
        {
            var ActionSvc = IocContainer.Resolve<IActionService>();
            var deviceID = ActionSvc.GetDeciveID();
            if (String.IsNullOrEmpty(deviceID))
            {
                Console.WriteLine("当前没有检测到手机");
                Console.WriteLine("请在手机 设置->开发者选项->打开开发都选项和USB调试 后继续运行此程序");
                Console.ReadKey();
                return;
            }
            var targetScore = 100;
            Console.WriteLine("如果累积误差比较大，请停止此程序。可以运行半自动版本消除误差后再重新运行此版本");
            Console.WriteLine("请输入您希望跳的目标分数（当达到此分数后，程序自动停止）");
            var inputScore = Console.ReadLine();
            targetScore = int.Parse(inputScore);

            var Model = new AutoCacheModel();
            var rand = new Random();
            while(true)
            {
                var bitImg = ActionSvc.GetScreenshots();

                Model.Image = new WidthHeight() { Width = bitImg.Width, Height = bitImg.Height };

                Image<Rgb, Byte> img = new Image<Rgb, Byte>(bitImg);
                Image<Rgb, Byte> sourceImg = new Image<Rgb, Byte>(bitImg);

                //原图宽的1/2
                var imgWidthCenter = (int)(img.Width / 2.0);
                //原图宽的1/3
                var imgWidthSplit = (int)(img.Width / 3.0);
                //原图高的1/3
                var imgHeightSplit = (int)(img.Height / 3.0);

                //成绩识别
                var sourceGrayImg = sourceImg.Convert<Gray, Byte>();
                Rectangle rectScore = new Rectangle(0, 0, imgWidthSplit * 2, imgHeightSplit);
                CvInvoke.cvSetImageROI(sourceGrayImg, rectScore);
                var scoreImg = new Image<Gray, Byte>(imgWidthSplit * 2, imgHeightSplit);
                CvInvoke.cvCopy(sourceGrayImg, scoreImg, IntPtr.Zero);
                var thresImg = scoreImg.ThresholdBinary(new Gray(78), new Gray(255));
                //thresImg.ToBitmap().Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DownLoad", DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".png"));
                Tesseract ocr = new Tesseract("", "num", OcrEngineMode.TesseractLstmCombined, "0123456789");
                ocr.SetImage(thresImg);
                ocr.SetVariable("tessedit_char_whitelist", "0123456789");
                if (ocr.Recognize() == 0)
                {
                    var val = ocr.GetUTF8Text();
                    val = val.Trim().Replace(" ", "");
                    if (!String.IsNullOrEmpty(val))
                    {
                        int score = Model.Score;
                        var canParse = int.TryParse(val, out score);
                        if (canParse && score > Model.Score) Model.Score = score;
                    }
                }

                if (Model.Score >= targetScore) break;

                var tempGrayPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Template", "Current.png");
                var tempGrayImg = new Image<Rgb, byte>(tempGrayPath);
                var match = img.MatchTemplate(tempGrayImg, TemplateMatchingType.CcorrNormed);

                double min = 0, max = 0;
                Point maxp = new Point(0, 0);//最好匹配的点
                Point minp = new Point(0, 0);
                CvInvoke.MinMaxLoc(match, ref min, ref max, ref minp, ref maxp);

                var startPoint = new Point();
                startPoint.X = maxp.X + (int)(tempGrayImg.Width / 2.0);
                startPoint.Y = maxp.Y + tempGrayImg.Height - 20;
                
                //裁剪查找区域
                //原图片1/3以下，小黑人以上
                var newImgStart = imgHeightSplit;
                var newImgEnd = maxp.Y + tempGrayImg.Height;
                var newImgHeight = newImgEnd - newImgStart;
                Rectangle rect = new Rectangle(0, newImgStart, img.Width, newImgHeight);

                CvInvoke.cvSetImageROI(sourceImg, rect);
                var newImg = new Image<Rgb, byte>(sourceImg.Width, newImgHeight);
                CvInvoke.cvCopy(sourceImg, newImg, IntPtr.Zero);

                //看小黑人在程序的左边还是右边
                //如果在左边，那目标点就在图片的右边
                bool targetInLeft = true;
                if (maxp.X < imgWidthCenter) targetInLeft = false;

                Rectangle halfRect;
                if (targetInLeft)
                    halfRect = new Rectangle(0, 0, imgWidthCenter, newImgHeight);
                else
                    halfRect = new Rectangle(imgWidthCenter, 0, imgWidthCenter, newImgHeight);

                CvInvoke.cvSetImageROI(newImg, halfRect);
                var halfImg = new Image<Rgb, byte>(imgWidthCenter, newImgHeight);
                CvInvoke.cvCopy(newImg, halfImg, IntPtr.Zero);
                
                Point topPoint = new Point();
                for (int i = 0; i < halfImg.Rows; i++)
                {
                    for (int j = 0; j < halfImg.Cols - 1; j++)
                    {
                        var cur = halfImg[i, j];
                        var next = halfImg[i, j + 1];
                        if (Math.Abs(RgbHelp.GetDiff(cur, next)) > 2)
                        {
                            var x = 2;
                            next = halfImg[i, j + x];
                            while (Math.Abs(RgbHelp.GetDiff(cur, next)) > 2)
                            {
                                x++;
                                next = halfImg[i, j + x];
                            }
                            topPoint.Y = i;
                            topPoint.X = j + (int)(x / 2.0);
                            break;
                        }
                    }
                    if (!topPoint.IsEmpty) break;
                }

                //这个顶点在原图中的位置
                var oldTopX = topPoint.X;
                if (!targetInLeft) oldTopX += imgWidthCenter;
                var oldTopY = topPoint.Y + imgHeightSplit;
                var oldTopPoint = new Point(oldTopX, oldTopY);
                
                Model.Top = oldTopPoint;
                Model.Start = startPoint;
                Console.WriteLine(JsonConvert.SerializeObject(Model));
                ActionSvc.Action(Model.Image, Model.Time);

                bitImg.Dispose();
                img.Dispose();
                sourceImg.Dispose();
                sourceGrayImg.Dispose();
                scoreImg.Dispose();
                thresImg.Dispose();
                tempGrayImg.Dispose();
                match.Dispose();
                newImg.Dispose();
                halfImg.Dispose();

                Thread.Sleep(Model.Time + rand.Next(600, 2000));
            }
        }
    }
    public static class RgbHelp
    {
        public static double GetDiff(Rgb one, Rgb two)
        {
            var diffR = one.Red - two.Red;
            var diffG = one.Green - two.Green;
            var diffB = one.Blue - two.Blue;
            return diffR + diffG + diffB;
        }
    }
}
