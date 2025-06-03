using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;
using HelixToolkit.Wpf;
using Xbim.IO.Memory;
using Xbim.ModelGeometry.Scene;
using Xbim.Tessellator;
using Xbim.Tessellator.MeshSimplification;

namespace Xbim.MeshSimplification.Visualizer
{
    public partial class MainWindow : Window
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<MainWindow> _logger;
        private MemoryModel? _model;
        private readonly List<XbimTriangulatedMesh> _originalMeshes = new();
        private List<XbimTriangulatedMesh> _currentMeshes = new();
        private readonly List<SimplificationStep> _simplificationSteps = new();
        private DispatcherTimer? _animationTimer;
        private int _currentAnimationStep = 0;
        private bool _isAnimating = false;
        private RenderingMode _renderingMode = RenderingMode.SolidWithEdges;

        public MainWindow()
        {
            InitializeComponent();
            
            _loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _logger = _loggerFactory.CreateLogger<MainWindow>();
            
            InitializeEventHandlers();
        }

        private void InitializeEventHandlers()
        {
            AnimationSpeedSlider.ValueChanged += (s, e) => 
            {
                AnimationSpeedText.Text = $"Speed: {AnimationSpeedSlider.Value:F1}x";
                if (_animationTimer != null)
                {
                    _animationTimer.Interval = TimeSpan.FromMilliseconds(1000 / AnimationSpeedSlider.Value);
                }
            };
        }

        private async void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "IFC Files (*.ifc)|*.ifc|All Files (*.*)|*.*",
                Title = "Select IFC File"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                await LoadIfcFile(openFileDialog.FileName);
            }
        }

        private async Task LoadIfcFile(string filePath)
        {
            try
            {
                _model?.Dispose();
                _model = MemoryModel.OpenRead(filePath);
                
                FileNameText.Text = Path.GetFileName(filePath);
                
                await Task.Run(() => GenerateGeometry());
                
                UpdateUI();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading IFC file");
                MessageBox.Show($"Error loading file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void GenerateGeometry()
        {
            if (_model == null) return;

            _originalMeshes.Clear();
            _currentMeshes.Clear();

            var context = new Xbim3DModelContext(_model);
            context.CreateContext(null, false, postTessellationCallback: (mesh, id) =>
            {
                if (mesh.TriangleCount > 0)
                {
                    _originalMeshes.Add(CopyMesh(mesh));
                    _currentMeshes.Add(CopyMesh(mesh));
                }
                return mesh;
            });
        }

        private void UpdateUI()
        {
            RenderMeshes(_currentMeshes);
            UpdateStatistics();
            
            // Enable controls if we have meshes loaded
            if (_originalMeshes.Count > 0)
            {
                var totalTriangles = _originalMeshes.Sum(m => (int)m.TriangleCount);
                var totalVertices = _originalMeshes.Sum(m => m.Vertices.Count());
                
                // Update info text
                TriangleCountText.Text = $"Triangles: {totalTriangles:N0}";
                VertexCountText.Text = $"Vertices: {totalVertices:N0}";
                
                // Set up target triangle slider
                TargetTriangleSlider.Maximum = totalTriangles;
                TargetTriangleSlider.Value = Math.Min(totalTriangles / 2, 1000);
                
                // Enable buttons
                SimplifyButton.IsEnabled = true;
                ResetButton.IsEnabled = true;
                AnimateButton.IsEnabled = true;
            }
            else
            {
                // Disable controls when no meshes loaded
                SimplifyButton.IsEnabled = false;
                ResetButton.IsEnabled = false;
                AnimateButton.IsEnabled = false;
            }
        }

        private void RenderMeshes(List<XbimTriangulatedMesh> meshes)
        {
            Viewport3D.Children.Clear();
            Viewport3D.Children.Add(new DefaultLights());

            // Render solid mesh for Solid and SolidWithEdges modes
            if (_renderingMode == RenderingMode.Solid || _renderingMode == RenderingMode.SolidWithEdges)
            {
                var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(135, 206, 250)));

                foreach (var mesh in meshes)
                {
                    var meshGeometry = new MeshGeometry3D();
                    // Add vertices
                    foreach (var vertex in mesh.Vertices)
                    {
                        meshGeometry.Positions.Add(new Point3D(vertex.X, vertex.Y, vertex.Z));
                    }

                    // Add triangles
                    foreach (var faceGroup in mesh.Faces)
                    {
                        foreach (var triangle in faceGroup.Value)
                        { 
                            // Access vertices following the edge chain, matching the Normal property pattern
                            var v0 = triangle[0].StartVertexIndex;
                            var v1 = triangle[0].NextEdge.StartVertexIndex;
                            var v2 = triangle[0].NextEdge.NextEdge.StartVertexIndex;
                            
                            meshGeometry.TriangleIndices.Add(v0);
                            meshGeometry.TriangleIndices.Add(v1);
                            meshGeometry.TriangleIndices.Add(v2);
                        }
                    }

                    meshGeometry.Normals = meshGeometry.CalculateNormals();
                    var geometryModel = new GeometryModel3D(meshGeometry, material);
                    geometryModel.BackMaterial = material;
                    var modelVisual = new ModelVisual3D { Content = geometryModel };
                    Viewport3D.Children.Add(modelVisual);
                }
            }

            // Render wireframe for Wireframe and SolidWithEdges modes
            if (_renderingMode == RenderingMode.Wireframe || _renderingMode == RenderingMode.SolidWithEdges)
            {
                var linesVisual = new LinesVisual3D
                {
                    Color = _renderingMode == RenderingMode.SolidWithEdges ? Colors.Black : Colors.Blue,
                    Thickness = _renderingMode == RenderingMode.SolidWithEdges ? 0.5 : 1.0
                };
                
                var points = new Point3DCollection();
                
                foreach (var mesh in meshes)
                {
                    var vertices = mesh.Vertices.ToArray();
                    
                    // Add wireframe lines for each triangle
                    foreach (var faceGroup in mesh.Faces)
                    {
                        foreach (var triangle in faceGroup.Value)
                        {
                            // Get vertex indices following the edge chain
                            var v0 = triangle[0].StartVertexIndex;
                            var v1 = triangle[0].NextEdge.StartVertexIndex;
                            var v2 = triangle[0].NextEdge.NextEdge.StartVertexIndex;
                            
                            var p0 = new Point3D(vertices[v0].X, vertices[v0].Y, vertices[v0].Z);
                            var p1 = new Point3D(vertices[v1].X, vertices[v1].Y, vertices[v1].Z);
                            var p2 = new Point3D(vertices[v2].X, vertices[v2].Y, vertices[v2].Z);
                            
                            // Add triangle edges
                            points.Add(p0); points.Add(p1);
                            points.Add(p1); points.Add(p2);
                            points.Add(p2); points.Add(p0);
                        }
                    }
                }
                
                linesVisual.Points = points;
                Viewport3D.Children.Add(linesVisual);
            }

            // Auto-fit camera
            Viewport3D.ZoomExtents();
        }

        private void TargetTriangleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TargetTriangleText != null)
            {
                TargetTriangleText.Text = $"Target: {(int)TargetTriangleSlider.Value:N0} triangles";
            }
        }

        private void ReductionFactorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ReductionFactorText != null)
            {
                ReductionFactorText.Text = $"Factor: {ReductionFactorSlider.Value * 100:F0}%";
                
                if (_originalMeshes.Count > 0)
                {
                    var totalTriangles = _originalMeshes.Sum(m => (int)m.TriangleCount);
                    TargetTriangleSlider.Value = (int)(totalTriangles * ReductionFactorSlider.Value);
                }
            }
        }

        private async void SimplifyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_originalMeshes.Count == 0) return;

            try
            {
                StatusText.Text = "Simplifying meshes...";
                ProgressBar.Visibility = Visibility.Visible;
                ProgressBar.IsIndeterminate = true;

                var targetTriangles = (int)TargetTriangleSlider.Value;
                
                // Run mesh simplification on background thread, but update UI on UI thread
                await Task.Run(() => SimplifyMeshes(targetTriangles));
                
                // Update UI on the UI thread
                await Dispatcher.InvokeAsync(() => {
                    RenderMeshes(_currentMeshes);
                    UpdateStatistics();
                    StatusText.Text = "Mesh simplification complete";
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error simplifying meshes");
                MessageBox.Show($"Error simplifying meshes: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Simplification failed";
            }
            finally
            {
                ProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void SimplifyMeshes(int targetTriangles)
        {
            _currentMeshes.Clear();
            
            var totalOriginalTriangles = _originalMeshes.Sum(m => (int)m.TriangleCount);
            
            foreach (var originalMesh in _originalMeshes)
            {
                var meshTargetTriangles = (int)((double)originalMesh.TriangleCount / totalOriginalTriangles * targetTriangles);
                meshTargetTriangles = Math.Max(meshTargetTriangles, 10); // Minimum triangles per mesh
                
                if (originalMesh.TriangleCount <= meshTargetTriangles)
                {
                    _currentMeshes.Add(CopyMesh(originalMesh));
                }
                else
                {
                    var simplifier = new XbimMeshSimplifier(originalMesh, (float)(_model?.ModelFactors.Precision ?? 1e-5));
                    var simplifiedMesh = simplifier.Simplify(meshTargetTriangles);
                    _currentMeshes.Add(simplifiedMesh);
                }
            }
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (_originalMeshes.Count == 0) return;

            _currentMeshes.Clear();
            foreach (var mesh in _originalMeshes)
            {
                _currentMeshes.Add(CopyMesh(mesh));
            }

            RenderMeshes(_currentMeshes);
            UpdateStatistics();
            StatusText.Text = "Reset to original mesh";
        }

        private async void AnimateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_originalMeshes.Count == 0) return;

            try
            {
                // Prepare animation steps on the UI thread since they involve UI-bound collections
                PrepareAnimationSteps();
                
                SetupAnimation();
                UpdateStepDisplay();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error preparing animation");
                MessageBox.Show($"Error preparing animation: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PrepareAnimationSteps()
        {
            _simplificationSteps.Clear();
            
            var steps = 20; // Number of animation steps
            var totalOriginalTriangles = _originalMeshes.Sum(m => (int)m.TriangleCount);
            var targetTriangles = (int)TargetTriangleSlider.Value;
            
            for (int step = 0; step <= steps; step++)
            {
                var progress = (double)step / steps;
                var currentTarget = (int)(totalOriginalTriangles - (totalOriginalTriangles - targetTriangles) * progress);
                
                var stepMeshes = new List<XbimTriangulatedMesh>();
                
                foreach (var originalMesh in _originalMeshes)
                {
                    var meshTargetTriangles = (int)((double)originalMesh.TriangleCount / totalOriginalTriangles * currentTarget);
                    meshTargetTriangles = Math.Max(meshTargetTriangles, 10);
                    
                    if (originalMesh.TriangleCount <= meshTargetTriangles)
                    {
                        stepMeshes.Add(CopyMesh(originalMesh));
                    }
                    else
                    {
                        var simplifier = new XbimMeshSimplifier(originalMesh, (float)(_model?.ModelFactors.Precision ?? 1e-5));
                        var simplifiedMesh = simplifier.Simplify(meshTargetTriangles);
                        stepMeshes.Add(simplifiedMesh);
                    }
                }
                
                _simplificationSteps.Add(new SimplificationStep
                {
                    StepNumber = step,
                    Meshes = stepMeshes,
                    TriangleCount = stepMeshes.Sum(m => (int)m.TriangleCount)
                });
            }
        }

        private void SetupAnimation()
        {
            StepSlider.Maximum = _simplificationSteps.Count - 1;
            StepSlider.IsEnabled = true;
            PlayButton.IsEnabled = true;
            StopButton.IsEnabled = true;
            
            UpdateStepDisplay();
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_simplificationSteps.Count == 0) return;

            _isAnimating = true;
            PlayButton.IsEnabled = false;
            PauseButton.IsEnabled = true;
            
            _animationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000 / AnimationSpeedSlider.Value)
            };
            _animationTimer.Tick += AnimationTimer_Tick;
            _animationTimer.Start();
            
            StatusText.Text = "Animation playing";
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            _animationTimer?.Stop();
            _isAnimating = false;
            PlayButton.IsEnabled = true;
            PauseButton.IsEnabled = false;
            StatusText.Text = "Animation paused";
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _animationTimer?.Stop();
            _isAnimating = false;
            _currentAnimationStep = 0;
            StepSlider.Value = 0;
            PlayButton.IsEnabled = true;
            PauseButton.IsEnabled = false;
            
            if (_simplificationSteps.Count > 0)
            {
                ShowAnimationStep(0);
            }
            
            StatusText.Text = "Animation stopped";
        }

        private void AnimationTimer_Tick(object? sender, EventArgs e)
        {
            _currentAnimationStep++;
            
            if (_currentAnimationStep >= _simplificationSteps.Count)
            {
                StopButton_Click(this, new RoutedEventArgs());
                return;
            }
            
            StepSlider.Value = _currentAnimationStep;
            ShowAnimationStep(_currentAnimationStep);
        }

        private void StepSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isAnimating && _simplificationSteps.Count > 0)
            {
                var step = (int)StepSlider.Value;
                ShowAnimationStep(step);
            }
            UpdateStepDisplay();
        }

        private void ShowAnimationStep(int step)
        {
            if (step < 0 || step >= _simplificationSteps.Count) return;
            
            var animationStep = _simplificationSteps[step];
            _currentMeshes = animationStep.Meshes.ToList();
            
            Dispatcher.Invoke(() =>
            {
                RenderMeshes(_currentMeshes);
                UpdateStatistics();
            });
        }

        private void UpdateStepDisplay()
        {
            if (StepText != null && _simplificationSteps.Count > 0)
            {
                StepText.Text = $"Step: {(int)StepSlider.Value} / {_simplificationSteps.Count - 1}";
            }
        }

        private void UpdateStatistics()
        {
            if (_originalMeshes.Count == 0) return;

            var originalTriangles = _originalMeshes.Sum(m => (int)m.TriangleCount);
            var currentTriangles = _currentMeshes.Sum(m => (int)m.TriangleCount);
            var reductionPercent = originalTriangles > 0 ? (1.0 - (double)currentTriangles / originalTriangles) * 100 : 0;

            OriginalTrianglesText.Text = $"Original: {originalTriangles:N0} triangles";
            CurrentTrianglesText.Text = $"Current: {currentTriangles:N0} triangles";
            ReductionPercentText.Text = $"Reduction: {reductionPercent:F1}%";
            
            // Simple quality metric based on triangle reduction
            var quality = reductionPercent < 50 ? "High" : reductionPercent < 80 ? "Medium" : "Low";
            QualityMetricText.Text = $"Quality: {quality}";
        }

        private void ResetCamera_Click(object sender, RoutedEventArgs e)
        {
            Viewport3D.ZoomExtents();
        }

        private void SolidMode_Click(object sender, RoutedEventArgs e)
        {
            _renderingMode = RenderingMode.Solid;
            UpdateRenderingModeMenu();
            RenderMeshes(_currentMeshes);
            StatusText.Text = "Solid mode enabled";
        }

        private void WireframeMode_Click(object sender, RoutedEventArgs e)
        {
            _renderingMode = RenderingMode.Wireframe;
            UpdateRenderingModeMenu();
            RenderMeshes(_currentMeshes);
            StatusText.Text = "Wireframe mode enabled";
        }

        private void SolidWithEdgesMode_Click(object sender, RoutedEventArgs e)
        {
            _renderingMode = RenderingMode.SolidWithEdges;
            UpdateRenderingModeMenu();
            RenderMeshes(_currentMeshes);
            StatusText.Text = "Solid with edges mode enabled";
        }

        private void UpdateRenderingModeMenu()
        {
            SolidMenuItem.IsChecked = _renderingMode == RenderingMode.Solid;
            WireframeMenuItem.IsChecked = _renderingMode == RenderingMode.Wireframe;
            SolidWithEdgesMenuItem.IsChecked = _renderingMode == RenderingMode.SolidWithEdges;
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Xbim Mesh Simplification Visualizer\n\n" +
                "This application demonstrates the mesh simplification algorithm in action.\n" +
                "Load an IFC file to see how the quadric error metric algorithm reduces triangle count while preserving shape.\n\n" +
                "Features:\n" +
                "• Real-time mesh simplification\n" +
                "• Step-by-step animation\n" +
                "• Quality metrics\n" +
                "• Interactive 3D visualization",
                "About", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        protected override void OnClosed(EventArgs e)
        {
            _animationTimer?.Stop();
            _model?.Dispose();
            base.OnClosed(e);
        }

        private XbimTriangulatedMesh CopyMesh(XbimTriangulatedMesh originalMesh)
        {
            var precision = (float)(_model?.ModelFactors.Precision ?? 1e-5);
            var newMesh = new XbimTriangulatedMesh((int)originalMesh.TriangleCount, precision);
            
            // Create a mapping of original vertex indices to new vertex indices
            var vertexMapping = new Dictionary<int, int>();
            var newVertexIndex = 0;
            
            // Copy vertices - vertices are just Vec3 objects
            foreach (var vertex in originalMesh.Vertices)
            {
                var newIndex = newMesh.AddVertex(vertex);
                vertexMapping[newVertexIndex++] = newIndex;
            }
            
            // Copy faces using the vertex mapping
            foreach (var faceGroup in originalMesh.Faces)
            {
                foreach (var triangle in faceGroup.Value)
                {
                    // Get vertex indices from the triangle edges
                    var v0 = vertexMapping[triangle[0].StartVertexIndex];
                    var v1 = vertexMapping[triangle[1].StartVertexIndex];
                    var v2 = vertexMapping[triangle[2].StartVertexIndex];
                    
                    // Add triangle with the face group key
                    newMesh.AddTriangle(v0, v1, v2, faceGroup.Key);
                }
            }
            
            newMesh.UnifyFaceOrientation();
            
            return newMesh;
        }
    }

    public enum RenderingMode
    {
        Solid,
        Wireframe,
        SolidWithEdges
    }

    public class SimplificationStep
    {
        public int StepNumber { get; set; }
        public List<XbimTriangulatedMesh> Meshes { get; set; } = new();
        public int TriangleCount { get; set; }
    }
} 