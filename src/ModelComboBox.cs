using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GitExtensions.CopilotCommitMessage;

internal sealed class ModelComboBox : ComboBox
{
    private readonly Timer _animationTimer = new() { Interval = 90 };
    private int _animationFrame;
    private bool _isLoading;

    internal ModelComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        DrawItem += OnDrawItem;
        _animationTimer.Tick += (_, _) =>
        {
            _animationFrame = (_animationFrame + 1) % 8;
            Invalidate();
        };
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (_isLoading == value)
            {
                return;
            }

            _isLoading = value;
            _animationFrame = 0;
            if (_isLoading)
            {
                _animationTimer.Start();
            }
            else
            {
                _animationTimer.Stop();
            }

            Invalidate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnDrawItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        string text = e.Index >= 0 ? GetItemText(Items[e.Index]) ?? string.Empty : Text;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        Color foreground = selected ? SystemColors.HighlightText : ForeColor;
        Rectangle textBounds = e.Bounds;
        textBounds.X += 3;
        textBounds.Width -= 6;

        bool drawSpinner = _isLoading && (e.State & DrawItemState.ComboBoxEdit) != 0;
        if (drawSpinner)
        {
            const int spinnerWidth = 22;
            textBounds.Width -= spinnerWidth;
            DrawSpinner(e.Graphics, e.Bounds.Right - spinnerWidth + 2, e.Bounds.Top + (e.Bounds.Height - 14) / 2, selected);
        }

        TextRenderer.DrawText(
            e.Graphics,
            text,
            Font,
            textBounds,
            foreground,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        e.DrawFocusRectangle();
    }

    private void DrawSpinner(Graphics graphics, int left, int top, bool selected)
    {
        SmoothingMode previousSmoothingMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color color = selected ? SystemColors.HighlightText : SystemColors.Highlight;
        for (int dot = 0; dot < 8; dot++)
        {
            int phase = (dot - _animationFrame + 8) % 8;
            int alpha = 45 + (7 - phase) * 28;
            using SolidBrush brush = new(Color.FromArgb(alpha, color));
            double angle = dot * Math.PI / 4;
            float x = left + 7 + (float)Math.Cos(angle) * 5 - 1.5f;
            float y = top + 7 + (float)Math.Sin(angle) * 5 - 1.5f;
            graphics.FillEllipse(brush, x, y, 3, 3);
        }

        graphics.SmoothingMode = previousSmoothingMode;
    }
}