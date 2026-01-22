from PIL import Image, ImageDraw

def create_icon():
    # Create a 256x256 image with transparent background
    img = Image.new('RGBA', (256, 256), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    
    # Maple-ish / Star-ish Polygon coordinates
    # Color: #FF7F50 (Coral/Orange), Outline: None
    center_x, center_y = 128, 128
    
    # A simplified stylized leaf shape
    points = [
        (128, 20),   # Top tip
        (150, 80),
        (210, 60),   # Right top tip
        (170, 110),
        (220, 160),  # Right mid tip
        (150, 160),
        (160, 240),  # Stem bottom
        (140, 240),  # Stem bottom width
        (128, 200),  # Stem top
        (116, 240),
        (96, 240),   # Stem
        (106, 160),
        (36, 160),   # Left mid tip
        (86, 110),
        (46, 60),    # Left top tip
        (106, 80)
    ]
    
    draw.polygon(points, fill='#D94E41') # Autumn Red
    
    # Save as ICO
    img.save('logo.ico', format='ICO', sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)])
    print("logo.ico created successfully.")

if __name__ == '__main__':
    create_icon()
