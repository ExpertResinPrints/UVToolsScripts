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

namespace UVtools.ScriptSample;


/// <summary>
/// Eliminate elephants foot in bottom layers and transition layers by dimming wall pixels.
/// Bottom layers and transition layers often use much higher exposures, leading to elephant's foot caused by increased light bleed.
/// Dimming wall pixels in the bottom and transition layers should result in an exposure equivalent to the normal layers' exposure.
/// If a dimming gradient is used, a smooth transition is applied between the effective wall exposure and overall layer exposure.
/// </summary>
public class ScriptElephantsFootDimming : ScriptGlobals
{
	// ToolScriptingControl.axaml.cs
	// ToolWindow : WindowEx
		
    #region Members
    private float _normalExposure;
	private uint _bottomLayerCount;
	private uint _transitionLayerCount;
    #endregion

    readonly ScriptNumericalInput<uint> WallThick = new()
    {
        Label = "Wall thickness",
        Unit = "pixels",
        Minimum = 1,
        Maximum = 500,
        Increment = 1,
        Value = 10,
    };

    readonly ScriptNumericalInput<float> ExposureInput = new()
    {
        Label = "Wall exposure",
        Unit = "s",
        Minimum = 1.0f,
        Maximum = 1000f,
        Increment = 0.01f,
        DecimalPlates = 2,
        Value = 3,
    };

	readonly ScriptToggleSwitchInput BottomAndTransitionLayers = new()
    {
        Label = "Apply to Bottom and Transition Layers (disregard layer range above)",
        ToolTip = "Apply to Bottom and Transition Layers (disregard layer range above)",
		Unit = "Apply to Bottom and Transition Layers (disregard layer range above)",
		Value = true,
    };
	
	readonly ScriptCheckBoxInput UseDimmingGradient = new()
    {
        Label = "Dimming Gradient",
        ToolTip = "If checked, a smooth transition will be applied on the wall interior to vary effective exposure from \"wall exposure\" to overall layer exposure",
        Value = true,
    };
	
	readonly ScriptNumericalInput<uint> GradientSize = new()
    {
	    Label = "Gradient Size",
        Unit = "pixels",
        Minimum = 1,
        Maximum = 100,
        Increment = 1,
        Value = 4,
    };
	
	readonly ScriptCheckBoxInput UseDynamicKernel = new()
    {
        Label = "Dynamic Kernel",
        ToolTip = "If checked, a dynamic kernel will be used to calculate the wall border",
        Value = true
    };

	public KernelConfiguration Kernel { get; set; } = new();

    /// <summary>
    /// Set configurations here, this function trigger just after a script loads
    /// </summary>
    public void ScriptInit()
    {
		_normalExposure = SlicerFile.ExposureTime;
		_bottomLayerCount = SlicerFile.BottomLayerCount;
		_transitionLayerCount = SlicerFile.TransitionLayerCount;
        Script.Name = "Elephant's Foot Dimming";
        Script.Description = "Dim wall pixels in bottom (" + _bottomLayerCount.ToString() + ") and transition layers (" + _transitionLayerCount.ToString() + ") to minimize elephant's foot caused by light bleed.\nWall pixels will be dimmed to provide an exposure equivalent to the chosen wall exposure.";
        Script.Author = "Expert Resin Prints";
        Script.Version = new Version(0, 1);
        Script.UserInputs.Add(WallThick);
        Script.UserInputs.Add(ExposureInput);
		Script.UserInputs.Add(BottomAndTransitionLayers);
		Script.UserInputs.Add(UseDimmingGradient);
		Script.UserInputs.Add(GradientSize);
		Script.UserInputs.Add(UseDynamicKernel);
		
		ExposureInput.Value = SlicerFile.ExposureTime;
		// Operation.SelectFirstToCurrentLayer(_bottomLayerCount + _transitionLayerCount);
    }

    /// <summary>
    /// Validate user inputs here, this function triggers when the user clicks on execute.
    /// </summary>
    /// <returns>An error message, empty or null if validation passes.</returns>
    public string? ScriptValidate()
    {
        StringBuilder sb = new();
        return sb.ToString();
    }

    /// <summary>
    /// Execute the script, this function triggers when the user clicks on execute and the validation passes.
    /// </summary>
    /// <returns>True if executes successfully to the end, otherwise false.</returns>
    public bool ScriptExecute()
    {
        float expMax = ExposureInput.Value;
        uint wallThickness = WallThick.Value;
		bool flagLayersBandT = BottomAndTransitionLayers.Value;
		bool flagDimmingGradient = UseDimmingGradient.Value;
		uint SizeGradient = GradientSize.Value;
		bool flagDynamicKernel = UseDynamicKernel.Value;

		// Linearly interpolate over a maximum of "SizeGradient" pixels or less than half of the total wall thickness
		uint num = Math.Min(SizeGradient,wallThickness / 2);
		float step = 1.0f/ ((float)num+1.0f);
		
		Kernel.UseDynamicKernel = flagDynamicKernel;

		if (flagLayersBandT == true){
			Operation.LayerIndexStart = 0;
			Operation.LayerIndexEnd = _bottomLayerCount + _transitionLayerCount - 1;
		}
		Progress.Reset("Wall dimming", Operation.LayerRangeCount); // Sets the progress name and number of items to process

        // Determine range of layers to operate on
        uint LayerIndexStart = Operation.LayerIndexStart;
        uint LayerIndexEnd = Operation.LayerIndexEnd;
        uint LayerRangeCount = LayerIndexEnd - LayerIndexStart + 1;
		
		// var result3 = MessageBoxManager.Standard.ShowDialog(
        //     "This is my script\n",
		//     "bottomLayers = " + SlicerFile.BottomLayerCount.ToString() + "\n" +
		//     "transitionLayers = " + SlicerFile.TransitionLayerCount.ToString() + "\n" +
		//     "LayerIndexStart = " + LayerIndexStart.ToString() + "\n" +
		//     "LayerIndexEnd = " + LayerIndexEnd.ToString() + "\n" +
        //     "SlicerFile.layers.length = " + SlicerFile.Layers.Length.ToString() + "\n" +
        //     "", AbstractMessageBoxStandard.MessageButtons.YesNo).Result;
		
		// Loop user selected layers in parallel
		Parallel.For(Operation.LayerIndexStart, Operation.LayerIndexEnd+1, CoreSettings.GetParallelOptions(Progress), layerIndex =>
		{
			Progress.PauseIfRequested();
			var layer = SlicerFile[layerIndex];
			
			// Select wall brightness based on normal layer exposure and user selected wall exposure
			Mat patternMatMask = null!;
			ushort brightness = 255;
			ushort brightness2 = 255;
			float expLayer = layer.ExposureTime;
			brightness = (ushort)Math.Round(255.0*Math.Max(0.1,Math.Min(1.0,expMax/expLayer)));

			// var result3 = MessageBoxManager.Standard.ShowDialog(
			// 	"This is my script\n",
			// 	"layerIndex = " + layerIndex.ToString() + "\n" +
			// 	"expMax = " + expMax.ToString() + "\n" +
			// 	"expLayer = " + expLayer.ToString() + "\n" +
			// 	"brightness = " + brightness.ToString() + "\n" +
			// 	"", AbstractMessageBoxStandard.MessageButtons.YesNo).Result;

			
			using (var mat = layer.LayerMat)
			{
				// mat - original image which will be overwritten
				// original - copy of original image (never changes)
				// originalRoi - cropped area of original (never changes)
				// tempMat - cropped area at desired brightness
				// erode - set to interior areas
				// applyMask - set to wall areas using erode
				// target - cropped area of mat. Wall areas are updated using tempMat(of desired brightness) and applyMask(set to wall areas).
				using var original = mat.Clone();  // Create indepedent clone
				var originalRoi = Operation.GetRoiOrVolumeBounds(original);
				var target = Operation.GetRoiOrVolumeBounds(mat);  // Changes to target affect mat
				Mat? applyMask;
				Mat tempMat;

				// Apply dimming gradient
				int iterations = (int)wallThickness;
				int iterations2 = (int)wallThickness;
				if (flagDimmingGradient == true){
					for (uint i = 0; i < num; i++)
					{
						iterations2 = (int)(wallThickness - i);
						brightness2 = (ushort)Math.Round(step*((num-i)*(255 - brightness)) + brightness);
						patternMatMask = EmguExtensions.InitMat(Operation.GetRoiSizeOrDefault(Operation.OriginalBoundingRectangle), new MCvScalar(brightness2));
						
						using var erode2 = new Mat();
						applyMask = target.Clone();
						var kernel2 = Kernel.GetKernel(ref iterations2);
						// Erode uses the image in target and sets the eroded image to erode
						CvInvoke.Erode(target, erode2, kernel2, EmguExtensions.AnchorCenter, iterations2, BorderType.Reflect101, default);
						// Sets nonzero values in erode to black in applyMask
						applyMask.SetTo(EmguExtensions.BlackColor, erode2);
						
						// Set model walls in "target" to desired pixel brightness (of "tempMat") using "applyMask"
						tempMat = patternMatMask;
						tempMat.CopyTo(target, applyMask);
					}
				}

				// Apply user selected wall exposure
				using var erode = new Mat();
				applyMask = target.Clone();
				if (flagDimmingGradient == true){ iterations -= (int)num; };
				var kernel = Kernel.GetKernel(ref iterations);
				// Erode uses the image in target and sets the eroded image to erode
				CvInvoke.Erode(target, erode, kernel, EmguExtensions.AnchorCenter, iterations, BorderType.Reflect101, default);
				// Sets nonzero values in erode to black in applyMask
				applyMask.SetTo(EmguExtensions.BlackColor, erode);
				
				// Set model walls in "target" to desired pixel brightness (of "tempMat") using "applyMask"
				patternMatMask = EmguExtensions.InitMat(Operation.GetRoiSizeOrDefault(Operation.OriginalBoundingRectangle), new MCvScalar(brightness));
				tempMat = patternMatMask;
				tempMat.CopyTo(target, applyMask);

				// Ignore areas smaller than a threshold
				// Copy ignored areas from "originalRoi" to "target"
				uint _ignoreAreaThreshold = 0;
				originalRoi.CopyAreasSmallerThan(_ignoreAreaThreshold, target);
				
				Operation.ApplyMask(original, mat);
				SlicerFile[layerIndex].LayerMat = mat;
				if (applyMask is not null && !ReferenceEquals(applyMask, target)) applyMask.Dispose();
			}

			Progress.LockAndIncrement();
		});

        // return true if not cancelled by user
        return !Progress.Token.IsCancellationRequested;
    }
}
