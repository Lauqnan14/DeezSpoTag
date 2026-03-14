package main

import (
    "flag"
    "fmt"
    "image"
    "image/draw"
    "image/jpeg"
    "image/png"
    "os"
    "strconv"
    "strings"

    pigo "github.com/esimov/pigo/core"
)

type options struct {
    input   string
    output  string
    model   string
    ratio   string
    quality int
}

func main() {
    opts := parseFlags()
    if opts.input == "" || opts.output == "" || opts.model == "" {
        fmt.Fprintln(os.Stderr, "--input, --output, and --model are required")
        os.Exit(2)
    }

    img, err := readImage(opts.input)
    if err != nil {
        fmt.Fprintln(os.Stderr, "failed to read image:", err)
        os.Exit(1)
    }

    classifier, err := loadClassifier(opts.model)
    if err != nil {
        fmt.Fprintln(os.Stderr, "failed to load model:", err)
        os.Exit(1)
    }

    bounds := img.Bounds()
    if bounds.Empty() {
        fmt.Fprintln(os.Stderr, "invalid image bounds")
        os.Exit(1)
    }

    aspect := parseAspectRatio(opts.ratio)
    face := detectLargestFace(classifier, img)
    crop := computeCrop(bounds, face, aspect)

    cropped := cropImage(img, crop)
    if err := writeJPEG(opts.output, cropped, opts.quality); err != nil {
        fmt.Fprintln(os.Stderr, "failed to write output:", err)
        os.Exit(1)
    }
}

func parseFlags() options {
    var opts options
    flag.StringVar(&opts.input, "input", "", "input image path")
    flag.StringVar(&opts.output, "output", "", "output image path")
    flag.StringVar(&opts.model, "model", "", "pigo facefinder model path")
    flag.StringVar(&opts.ratio, "ratio", "16:9", "target aspect ratio (e.g. 16:9)")
    flag.IntVar(&opts.quality, "quality", 85, "jpeg quality 1-100")
    flag.Parse()
    if opts.quality < 1 || opts.quality > 100 {
        opts.quality = 85
    }
    return opts
}

func readImage(path string) (image.Image, error) {
    file, err := os.Open(path)
    if err != nil {
        return nil, err
    }
    defer file.Close()

    img, _, err := image.Decode(file)
    if err != nil {
        return nil, err
    }

    return img, nil
}

func loadClassifier(modelPath string) (*pigo.Pigo, error) {
    cascade, err := os.ReadFile(modelPath)
    if err != nil {
        return nil, err
    }
    p := pigo.NewPigo()
    return p.Unpack(cascade)
}

func detectLargestFace(classifier *pigo.Pigo, img image.Image) image.Rectangle {
    pixels := pigo.RgbToGrayscale(img)
    bounds := img.Bounds()
    cols := bounds.Dx()
    rows := bounds.Dy()

    minSize := int(float64(cols) * 0.06)
    if rows < cols {
        minSize = int(float64(rows) * 0.06)
    }
    if minSize < 20 {
        minSize = 20
    }

    maxSize := cols
    if rows < cols {
        maxSize = rows
    }

    cParams := pigo.CascadeParams{
        MinSize:     minSize,
        MaxSize:     maxSize,
        ShiftFactor: 0.1,
        ScaleFactor: 1.05,
        ImageParams: pigo.ImageParams{
            Pixels: pixels,
            Rows:   rows,
            Cols:   cols,
            Dim:    cols,
        },
    }

    dets := classifier.RunCascade(cParams, 0.0)
    dets = classifier.ClusterDetections(dets, 0.2)

    var best *pigo.Detection
    for i := range dets {
        det := dets[i]
        if det.Q <= 0 {
            continue
        }
        if best == nil || det.Scale*det.Scale > best.Scale*best.Scale {
            best = &det
        }
    }

    if best == nil {
        return image.Rectangle{}
    }

    half := best.Scale / 2
    left := best.Col - half
    top := best.Row - half
    right := best.Col + half
    bottom := best.Row + half

    return image.Rect(left, top, right, bottom)
}

func computeCrop(bounds image.Rectangle, face image.Rectangle, aspect float64) image.Rectangle {
    width := bounds.Dx()
    height := bounds.Dy()

    if aspect <= 0 {
        aspect = float64(width) / float64(height)
    }

    targetW := width
    targetH := int(float64(targetW) / aspect)
    if targetH > height {
        targetH = height
        targetW = int(float64(targetH) * aspect)
    }

    centerX := bounds.Min.X + width/2
    centerY := bounds.Min.Y + height/2
    if !face.Empty() {
        centerX = (face.Min.X + face.Max.X) / 2
        centerY = (face.Min.Y + face.Max.Y) / 2
        centerY -= int(float64(targetH) * 0.15)
    }

    left := centerX - targetW/2
    top := centerY - targetH/2

    if left < bounds.Min.X {
        left = bounds.Min.X
    }
    if top < bounds.Min.Y {
        top = bounds.Min.Y
    }

    right := left + targetW
    bottom := top + targetH

    if right > bounds.Max.X {
        right = bounds.Max.X
        left = right - targetW
    }
    if bottom > bounds.Max.Y {
        bottom = bounds.Max.Y
        top = bottom - targetH
    }

    return image.Rect(left, top, right, bottom)
}

func cropImage(img image.Image, rect image.Rectangle) image.Image {
    rect = rect.Intersect(img.Bounds())
    if rect.Empty() {
        return img
    }

    dst := image.NewRGBA(image.Rect(0, 0, rect.Dx(), rect.Dy()))
    draw.Draw(dst, dst.Bounds(), img, rect.Min, draw.Src)
    return dst
}

func writeJPEG(path string, img image.Image, quality int) error {
    file, err := os.Create(path)
    if err != nil {
        return err
    }
    defer file.Close()

    return jpeg.Encode(file, img, &jpeg.Options{Quality: quality})
}

func parseAspectRatio(value string) float64 {
    parts := strings.Split(value, ":")
    if len(parts) != 2 {
        return 0
    }
    w, err1 := strconv.ParseFloat(parts[0], 64)
    h, err2 := strconv.ParseFloat(parts[1], 64)
    if err1 != nil || err2 != nil || w <= 0 || h <= 0 {
        return 0
    }
    return w / h
}

func init() {
    image.RegisterFormat("jpeg", "jpeg", jpeg.Decode, jpeg.DecodeConfig)
    image.RegisterFormat("jpg", "jpg", jpeg.Decode, jpeg.DecodeConfig)
    image.RegisterFormat("png", "png", png.Decode, png.DecodeConfig)
}
