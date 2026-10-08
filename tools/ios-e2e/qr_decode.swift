// Prints the text of every QR code in a PNG (CoreImage's detector). usage: swift qr_decode.swift <file.png> (tools/ios-e2e/ios_e2e.py reads the phone code off the simulator screen with it)
import CoreImage
import Foundation

let url = URL(fileURLWithPath: CommandLine.arguments[1])
guard let image = CIImage(contentsOf: url) else { print("cannot read image"); exit(1) }
let detector = CIDetector(ofType: CIDetectorTypeQRCode, context: nil, options: [CIDetectorAccuracy: CIDetectorAccuracyHigh])!
for case let feature as CIQRCodeFeature in detector.features(in: image) {
    if let text = feature.messageString { print(text) }
}
