/*
 *                     GNU AFFERO GENERAL PUBLIC LICENSE
 *                       Version 3, 19 November 2007
 *  Copyright (C) 2007 Free Software Foundation, Inc. <https://fsf.org/>
 *  Everyone is permitted to copy and distribute verbatim copies
 *  of this license document, but changing it is not allowed.
 */
 // Authored by "Expert Resin Prints"
 // July 29th, 2023

using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UVtools.Core;
using UVtools.Core.Exceptions;
using UVtools.Core.Extensions;
using UVtools.Core.Dialogs;
using UVtools.Core.FileFormats;
using UVtools.Core.Layers;
using UVtools.Core.Managers;
using UVtools.Core.Objects;
using UVtools.Core.Scripting;
using System.Windows.Forms; // for message box

namespace UVtools.ScriptSample;

/// <summary>
/// Test multiple expsoures in a single resin print.
/// The script separates the build plate into regions and applies different exposures to each region.
/// Variation in exposure is achieved either through pixel dimming or subsequent exposures at the same layer height.
/// </summary>
public class ScriptExposureFinderByGrid : ScriptGlobals
{
    #region Members
	private float _normalExposure;
    #endregion

    readonly ScriptCheckBoxInput UsePixelDimming = new()
    {
        Label = "PixelDimming",
        ToolTip = "If unchecked, separate layers will be generated at the same layer height; if checked, pixel dimming will be used",
        Value = true
    };

	readonly ScriptNumericalInput<uint> GridInputX = new()
    {
        Label = "Grid Size (x dir.)",
        Unit = "divisions",
        Minimum = 1,
        Maximum = 32,
        Increment = 1,
        Value = 4,
    };

    readonly ScriptNumericalInput<uint> GridInputY = new()
    {
        Label = "Grid Size (y dir.)",
        Unit = "divisions",
        Minimum = 1,
        Maximum = 32,
        Increment = 1,
        Value = 2,
    };

    readonly ScriptTextBoxInput ExposureInput = new()
    {
        Label = "Exposures",
        ToolTip = "Exposures to be tested; Should not exceed the number of regions",
        Unit = "number",
        Value = "2.00,2.25,2.50,2.75,3.00,3.25,3.50,3.75"
    };

    public double[] Exposures
    {
        get
        {
            List<double> levels = new();
            var split = ExposureInput.Value!.Split(',', StringSplitOptions.TrimEntries);
            foreach (var str in split)
            {
                if(!double.TryParse(str, out var exposure)) continue;
                if(exposure is double.MinValue or double.MaxValue) continue;
                levels.Add(exposure);
            }
            return levels.ToArray();
        }
    }

    /// <summary>
    /// Set configurations here, this function triggers just after a script loads
    /// </summary>
    public void ScriptInit()
    {
		_normalExposure = SlicerFile.ExposureTime;
        Script.Name = "Exposure Finder by Grid";
        Script.Description = "Apply a separate exposure to a grid pattern on the buid platform.\nThe normal exposure is " + _normalExposure.ToString() + " and will be overwritten.";
        Script.Author = "Expert Resin Prints";
        Script.Version = new Version(0, 1);
        Script.UserInputs.Add(UsePixelDimming);
		Script.UserInputs.Add(GridInputX);
		Script.UserInputs.Add(GridInputY);
        Script.UserInputs.Add(ExposureInput);
    }

    /// <summary>
    /// Validate user inputs here, this function triggers when the user clicks on execute.
    /// </summary>
    /// <returns>An error message, empty or null if validation passes.</returns>
    public string? ScriptValidate()
    {
        StringBuilder sb = new();
            
        if (Exposures.Length == 0)
        {
            sb.AppendLine($"No exposure times are set");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Execute the script, this function triggers when the user clicks on execute and the validation passes.
    /// </summary>
    /// <returns>True if executes successfully to the end, otherwise false.</returns>
    public bool ScriptExecute()
    {
        Progress.Reset("Exposure finder", Operation.LayerRangeCount); // Sets the progress name and number of items to process
        bool flagDimming = UsePixelDimming.Value;
        double[] exposureArr = Exposures;

        // Exposure information
        double expMax = exposureArr.Max();

        // Determine grid size
        Mat mask = SlicerFile.CreateMat(true);
        uint nx = GridInputX.Value;
        uint ny = GridInputY.Value;
        uint ntotal = nx * ny;  // between 1 and 32*32
        uint nmax = Math.Min(ntotal, (uint) exposureArr.Length);
        double xStep = (double)mask.Size.Width / (double)nx;
        double yStep = (double)mask.Size.Height / (double)ny;

        // Determine range of layers to operate on
        uint LayerIndexStart = Operation.LayerIndexStart;
        uint LayerIndexEnd = Operation.LayerIndexEnd;
        uint LayerRangeCount = LayerIndexEnd - LayerIndexStart + 1;

        // Allocate new layers
        var layers = new Layer[SlicerFile.LayerCount + LayerRangeCount*(nmax-1)];
        if (!flagDimming)
        {
            // Untouched: Insert layers below selected layer range
            for (uint i = 0; i < LayerIndexStart; i++)
            {
                layers[i] = SlicerFile[i];
            }
        }
        int bottomLayers = SlicerFile.BottomLayerCount;

        //var result3 = MessageBoxManager.Standard.ShowDialog(
        //    "This is my script\n",
        //    "SlicerFile.layers.length = " + SlicerFile.Layers.Length.ToString() + "\n" +
        //    "LayerRangeCount = " + LayerRangeCount.ToString() + "\n" +
        //    "nmax = " + nmax.ToString() + "\n" +
        //    "", AbstractMessageBoxStandard.MessageButtons.YesNo).Result;

        // Generate "mask" for dimmming and "mask2" for subsequent layers corresponding to each grid region at same build height
        List<Mat> mask2 = new List<Mat>();
        for (uint k = 0; k < nmax; k++)
        {
            mask2.Add(SlicerFile.CreateMat(true));
        }
		for (uint j = 0; j < ny; j++) {
			double y = (double)j * yStep;
			for (uint i = 0; i < nx; i++) {
				double x = (double)i * xStep;
				int dx = Convert.ToInt32(Math.Round(x + xStep)) - Convert.ToInt32(Math.Round(x));
				int dy = Convert.ToInt32(Math.Round(y + yStep)) - Convert.ToInt32(Math.Round(y));
				uint k = j * nx + i;
				ushort brightness = 255;
				if (k < nmax)
				{
					brightness = (ushort)Math.Round(255.0*exposureArr[k]/expMax);
					CvInvoke.Rectangle(mask2[(int)k],
						new Rectangle(Convert.ToInt32(Math.Round(x)), Convert.ToInt32(Math.Round(y)), dx, dy),
						EmguExtensions.WhiteColor,
						-1, LineType.FourConnected);
				}
				CvInvoke.Rectangle(mask,
					new Rectangle(Convert.ToInt32(Math.Round(x)), Convert.ToInt32(Math.Round(y)), dx, dy),
					new MCvScalar(brightness),
					-1, LineType.FourConnected);
			}
		}


		// Loop user selected layers in parallel
		Parallel.For(Operation.LayerIndexStart, Operation.LayerIndexEnd+1, CoreSettings.GetParallelOptions(Progress), layerIndex =>
		{
			Progress.PauseIfRequested();
			var firstLayer = SlicerFile[layerIndex]; // Unpack and expose layer variable for easier use
			using var mat = firstLayer.LayerMat;     // Gets this layer mat/image

			if (flagDimming)
			{
				// Create multiple exposures through dimming and set to current layer
				CvInvoke.Multiply(mat, mask, mat,1.0/255.0);
				firstLayer.LayerMat = mat;
				firstLayer.ExposureTime = (float)expMax;
				SlicerFile[layerIndex] = firstLayer;
			}
			else
			{
				// Create multiple exposures using multiple layers at same height
				var secondLayerCopy = firstLayer.Clone();
				var isBottomLayer = firstLayer.IsBottomLayer;

				for (uint k = 1; k < nmax; k++)
				{
					// Create and add a layer
					// Clone all layer properties such as expposure and lift height
					var secondLayer = firstLayer.Clone();

					if (isBottomLayer)
					{
						Interlocked.Increment(ref bottomLayers);
					}

					// Set a small lift height and standard lift speed for subsequent layers
					if (SlicerFile.SupportPerLayerSettings)
					{
						secondLayer.LiftHeightTotal = (float)0.1;
						secondLayer.SetWaitTimeBeforeCureOrLightOffDelay((float)0.0);
					}

					using var mat2 = secondLayer.LayerMat;
					uint pos2 = LayerIndexStart + ((uint)layerIndex - LayerIndexStart) * nmax + k;
					CvInvoke.BitwiseAnd(mat2, mask2[(int)k], mat2);
					secondLayer.LayerMat = mat2;
					secondLayer.ExposureTime = (float)exposureArr[(int)k];
					layers[pos2] = secondLayer;
				}

				uint pos = LayerIndexStart + ((uint)layerIndex - LayerIndexStart) * nmax;
				CvInvoke.BitwiseAnd(mat, mask2[0], mat);
				firstLayer.LayerMat = mat;
				firstLayer.ExposureTime = (float)exposureArr[0];
				layers[pos] = firstLayer;
			}

			Progress.LockAndIncrement();
		});

        if (!flagDimming)
        {
			// Untouched: Insert layers above selected layer range
            for (uint i = LayerIndexEnd + 1; i < SlicerFile.LayerCount; i++)
            {
                uint index_tmp = i + LayerRangeCount * (nmax - 1);
                layers[i + LayerRangeCount * (nmax-1)] = SlicerFile[i];
            }

            SlicerFile.SuppressRebuildPropertiesWork(() =>
            {
                SlicerFile.BottomLayerCount = (ushort)bottomLayers;
                SlicerFile.Layers = layers;
            });
        }
		
        //var result2 = MessageBoxManager.Standard.ShowDialog(
        //    "At the end of the script.\n",
        //    "SlicerFile.layers.length = " + SlicerFile.Layers.Length.ToString() + "\n" +
        //    "", AbstractMessageBoxStandard.MessageButtons.YesNo).Result;

        //for (uint k2 = 0; k2 < SlicerFile.LayerCount; k2++)
        //{
        //    var result20 = MessageBoxManager.Standard.ShowDialog(
        //        "At the end of the script.\n",
        //        "SlicerFile.layers[" + k2.ToString() + "].PositionZ = " + SlicerFile.Layers[k2].PositionZ.ToString() + "\n" +
        //        "", AbstractMessageBoxStandard.MessageButtons.YesNo).Result;
        //}
            

        // return true if not cancelled by user
        return !Progress.Token.IsCancellationRequested;
    }
}