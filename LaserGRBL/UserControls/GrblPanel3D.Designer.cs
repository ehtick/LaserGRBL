﻿namespace LaserGRBL.UserControls
{
    partial class GrblPanel3D
    {
        /// <summary> 
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        /// <summary> 
        /// Required method for Designer support - do not modify 
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
			this.components = new System.ComponentModel.Container();
			this.TimIssueDetector = new System.Windows.Forms.Timer(this.components);
			this.SuspendLayout();
			// 
			// TimIssueDetector
			// 
			this.TimIssueDetector.Interval = 5000;
			this.TimIssueDetector.Tick += new System.EventHandler(this.TimIssueDetector_Tick);
			// 
			// GrblPanel3D
			// 
			this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
			this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
			this.BackColor = System.Drawing.SystemColors.Control;
			this.Name = "GrblPanel3D";
			this.Size = new System.Drawing.Size(1170, 856);
			this.Load += new System.EventHandler(this.GrblPanel3D_Load);
			this.ResumeLayout(false);

        }

		#endregion

		private System.Windows.Forms.Timer TimIssueDetector;
	}
}