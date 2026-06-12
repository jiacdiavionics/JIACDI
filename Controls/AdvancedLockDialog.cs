using System;
using System.Drawing;
using System.Windows.Forms;

namespace MissionPlanner.Controls
{
    public class AdvancedLockDialog : Form
    {
        private Label lblStatus;
        private Button btnLock;
        private Button btnUnlock;
        private Button btnCancel;
        private bool _resultLocked = false;
        
        public bool ResultLocked
        {
            get { return _resultLocked; }
        }
        
        public AdvancedLockDialog(bool currentlyLocked)
        {
            InitializeComponent();
            
            if (currentlyLocked)
            {
                lblStatus.Text = "Advanced menus are currently LOCKED.\nClick 'Unlock' to enter password and unlock, or 'Lock' to keep them locked.";
                btnLock.Visible = true;
                btnUnlock.Text = "Unlock";
            }
            else
            {
                lblStatus.Text = "Advanced menus are currently UNLOCKED.\nClick 'Lock' to lock them, or 'Unlock' to keep them unlocked.";
                btnLock.Text = "Lock";
                btnUnlock.Visible = true;
            }
        }
        
        private void InitializeComponent()
        {
            this.lblStatus = new System.Windows.Forms.Label();
            this.btnLock = new System.Windows.Forms.Button();
            this.btnUnlock = new System.Windows.Forms.Button();
            this.btnCancel = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // lblStatus
            // 
            this.lblStatus.AutoSize = true;
            this.lblStatus.Location = new System.Drawing.Point(12, 9);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(300, 40);
            this.lblStatus.TabIndex = 0;
            this.lblStatus.Text = "Advanced menus are currently LOCKED.";
            this.lblStatus.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // btnLock
            // 
            this.btnLock.Location = new System.Drawing.Point(12, 60);
            this.btnLock.Name = "btnLock";
            this.btnLock.Size = new System.Drawing.Size(100, 30);
            this.btnLock.TabIndex = 1;
            this.btnLock.Text = "Lock";
            this.btnLock.UseVisualStyleBackColor = true;
            this.btnLock.Click += new System.EventHandler(this.btnLock_Click);
            // 
            // btnUnlock
            // 
            this.btnUnlock.Location = new System.Drawing.Point(118, 60);
            this.btnUnlock.Name = "btnUnlock";
            this.btnUnlock.Size = new System.Drawing.Size(100, 30);
            this.btnUnlock.TabIndex = 2;
            this.btnUnlock.Text = "Unlock";
            this.btnUnlock.UseVisualStyleBackColor = true;
            this.btnUnlock.Click += new System.EventHandler(this.btnUnlock_Click);
            // 
            // btnCancel
            // 
            this.btnCancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.btnCancel.Location = new System.Drawing.Point(224, 60);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(100, 30);
            this.btnCancel.TabIndex = 3;
            this.btnCancel.Text = "Cancel";
            this.btnCancel.UseVisualStyleBackColor = true;
            // 
            // AdvancedLockDialog
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(336, 102);
            this.Controls.Add(this.btnCancel);
            this.Controls.Add(this.btnUnlock);
            this.Controls.Add(this.btnLock);
            this.Controls.Add(this.lblStatus);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "AdvancedLockDialog";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Advanced Menu Lock";
            this.ResumeLayout(false);
            this.PerformLayout();
        }
        
        private void btnLock_Click(object sender, EventArgs e)
        {
            this._resultLocked = true;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
        
        private void btnUnlock_Click(object sender, EventArgs e)
        {
            this._resultLocked = false;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}